using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.Tests;
using Unity.Jobs;
using IceEvents;
using System;

namespace IceEvents.Tests
{
    public partial class EventSystemTests : ECSTestsFixture
    {
        #region Parallel Write - Boundary & Error Tests

        [Test]
        public void ParallelWrite_ZeroThreadCount_Throws()
        {
            World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<ParallelTestEvent>>();
            var buffer = m_Manager.CreateEntityQuery(typeof(EventBuffer<ParallelTestEvent>))
                .GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            Assert.Throws<ArgumentException>(() =>
                buffer.GetParallelWriter(0, Allocator.TempJob),
                "Should throw ArgumentException when threadCount is 0");
        }

        [Test]
        public void ParallelWrite_CapacitySufficient_NoExpansion()
        {
            World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<ParallelTestEvent>>();
            var buffer = m_Manager.CreateEntityQuery(typeof(EventBuffer<ParallelTestEvent>))
                .GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            // Ensure initial capacity is large enough
            int initialCapacity = 2048;
            buffer.ValueRW.BufferUpdateCurrent.SetCapacity(initialCapacity);

            // Write fewer items than capacity
            int writeCount = 100;
            var writerHandle = buffer.GetParallelWriter(writeCount, Allocator.TempJob);

            var job = new TestNoOpJobWithWrite
            {
                Writer = writerHandle.Writer,
                ItemCount = writeCount
            };

            var dependency = job.Schedule(writeCount, 1);
            writerHandle.ScheduleCommit(buffer, ref dependency);
            dependency.Complete();

            var resultBuffer = m_Manager.CreateEntityQuery(typeof(EventBuffer<ParallelTestEvent>))
                .GetSingleton<EventBuffer<ParallelTestEvent>>();

            Assert.AreEqual(writeCount, resultBuffer.BufferUpdateCurrent.Length);
            Assert.AreEqual(initialCapacity, resultBuffer.BufferUpdateCurrent.Capacity,
                "Capacity should not change if sufficient");
        }

        [Test]
        public void ParallelWrite_LargeThreadIndex_Handles()
        {
            World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<ParallelTestEvent>>();
            var buffer = m_Manager.CreateEntityQuery(typeof(EventBuffer<ParallelTestEvent>))
                .GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            int declaredThreadCount = 10;
            var writerHandle = buffer.GetParallelWriter(declaredThreadCount, Allocator.TempJob);

            // Accessing index out of range (>= declaredThreadCount)
            // NativeStream.Writer checks typically throw InvalidOperationException or IndexOutOfRangeException 
            // when safety checks are enabled.
            var writer = writerHandle.Writer;

            // We need to simulate this access. Since we can't easily catch exceptions thrown INSIDE a job 
            // from the main thread (they usually log errors or crash editor depending on settings), 
            // we will try to do it on main thread if possible, or verify safety system caches it.
            // NativeStream.Writer.BeginForEachIndex checks range.

            Assert.Throws<System.ArgumentException>(() =>
            {
                writer.BeginForEachIndex(declaredThreadCount); // Index 10 is out of bounds [0..9]
            }, "Should throw when accessing index >= declared count");

            // Clean up manually since we didn't schedule commit
            writerHandle.Dispose();
        }

        [Test]
        public void ParallelWrite_DoubleCommit_Throws()
        {
            World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<ParallelTestEvent>>();
            var buffer = m_Manager.CreateEntityQuery(typeof(EventBuffer<ParallelTestEvent>))
                .GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            var writerHandle = buffer.GetParallelWriter(1, Allocator.TempJob);
            JobHandle dependency = default;

            // First commit - should succeed
            writerHandle.ScheduleCommit(buffer, ref dependency);

            // Second commit - should fail because stream is already disposed/invalidated
            // NativeStream.Dispose(JobHandle) will throw if already disposed
            Assert.Throws<InvalidOperationException>(() =>
            {
                writerHandle.ScheduleCommit(buffer, ref dependency);
            }, "Should throw on double commit (double dispose of stream)");

            dependency.Complete();
        }

        #endregion

        #region Helper Jobs

        [BurstCompile]
        struct TestNoOpJobWithWrite : IJobParallelFor
        {
            public ParallelEventWriter<ParallelTestEvent> Writer;
            public int ItemCount;

            public void Execute(int index)
            {
                if (index < ItemCount)
                {
                    Writer.BeginForEachIndex(index);
                    Writer.Write(new ParallelTestEvent { Value = index });
                    Writer.EndForEachIndex();
                }
            }
        }

        #endregion
    }
}

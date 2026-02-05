using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Entities.Tests;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using IceEvents;
using System.Collections.Generic;
using UnityEngine;

[assembly: RegisterGenericComponentType(typeof(IceEvents.EventBuffer<IceEvents.Tests.TestEvent>))]
[assembly: RegisterGenericComponentType(typeof(IceEvents.EventBuffer<IceEvents.Tests.ParallelTestEvent>))]
[assembly: RegisterGenericJobType(typeof(IceEvents.EventCommitJob<IceEvents.Tests.TestEvent>))]
[assembly: RegisterGenericJobType(typeof(IceEvents.EventCommitJob<IceEvents.Tests.ParallelTestEvent>))]

namespace IceEvents.Tests
{
    public struct TestEvent : IEvent
    {
        public int Value;
    }

    public struct ParallelTestEvent : IEvent
    {
        public int ThreadIndex;
        public int Value;
    }

    [TestFixture]
    public partial class EventSystemTests : ECSTestsFixture
    {
        #region Setup & Helpers

        private EventBuffer<TestEvent> GetBuffer()
        {
            var query = m_Manager.CreateEntityQuery(typeof(EventBuffer<TestEvent>));
            return m_Manager.GetComponentData<EventBuffer<TestEvent>>(query.GetSingletonEntity());
        }

        private void SimulateUpdateFrame<T>() where T : unmanaged, IEvent
        {
            var sys = World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<T>>();
            sys.Update();
        }

        private void SimulateFixedUpdateFrame<T>() where T : unmanaged, IEvent
        {
            var sys = World.GetOrCreateSystemManaged<EventLifecycleFixedSystem<T>>();
            sys.Update();
        }

        #endregion

        #region EventReader - Basic Tests

        [Test]
        public void Reader_ContinuousReading_TracksNewEvents()
        {
            World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<TestEvent>>();
            var buffer = GetBuffer();
            var reader = buffer.GetUpdateReader(Allocator.Temp);
            reader.Update(buffer);

            var writer = buffer.GetWriter();
            writer.Write(new TestEvent { Value = 1 });
            writer.Write(new TestEvent { Value = 2 });

            var readSet = new HashSet<int>();
            foreach (var e in reader) readSet.Add(e.Value);

            Assert.AreEqual(2, readSet.Count);
            Assert.That(readSet.Contains(1));
            Assert.That(readSet.Contains(2));

            SimulateUpdateFrame<TestEvent>();
            buffer = GetBuffer();
            reader.Update(buffer);

            writer = buffer.GetWriter();
            writer.Write(new TestEvent { Value = 3 });

            readSet.Clear();
            foreach (var e in reader) readSet.Add(e.Value);

            Assert.AreEqual(1, readSet.Count);
            Assert.That(readSet.Contains(3));

            reader.Dispose();
        }

        [Test]
        public void Reader_CatchUp_ReadsMissedEvents()
        {
            World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<TestEvent>>();
            var buffer = GetBuffer();
            var reader = buffer.GetUpdateReader(Allocator.Temp);

            var writer = buffer.GetWriter();
            writer.Write(new TestEvent { Value = 1 });
            writer.Write(new TestEvent { Value = 2 });
            SimulateUpdateFrame<TestEvent>();

            buffer = GetBuffer();
            writer = buffer.GetWriter();
            writer.Write(new TestEvent { Value = 3 });
            writer.Write(new TestEvent { Value = 4 });

            reader.Update(buffer);

            var readSet = new HashSet<int>();
            foreach (var e in reader) readSet.Add(e.Value);

            Assert.AreEqual(4, readSet.Count);
            Assert.That(readSet.SetEquals(new[] { 1, 2, 3, 4 }));

            reader.Dispose();
        }

        [Test]
        public void Reader_PartialConsumption_BookmarkCorrect()
        {
            World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<TestEvent>>();
            var buffer = GetBuffer();

            var writer = buffer.GetWriter();
            writer.Write(new TestEvent { Value = 1 });
            writer.Write(new TestEvent { Value = 2 });
            writer.Write(new TestEvent { Value = 3 });

            var reader = buffer.GetUpdateReader(Allocator.Temp);
            reader.Update(buffer);

            int count = 0;
            foreach (var e in reader)
            {
                count++;
                if (count == 2) break;
            }

            Assert.AreEqual(2, count);

            reader.Update(buffer);
            var readSet = new HashSet<int>();
            foreach (var e in reader) readSet.Add(e.Value);

            Assert.AreEqual(1, readSet.Count);
            Assert.That(readSet.Contains(3));

            reader.Dispose();
        }

        [Test]
        public void Reader_EmptyBuffer_NoException()
        {
            World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<TestEvent>>();
            var buffer = GetBuffer();
            var reader = buffer.GetUpdateReader(Allocator.Temp);
            reader.Update(buffer);

            int count = 0;
            foreach (var e in reader) count++;

            Assert.AreEqual(0, count);

            reader.Dispose();
        }

        #endregion

        #region Dual Channel Tests

        [Test]
        public void DualChannel_NoEventLost_AtDifferentRates()
        {
            World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<TestEvent>>();
            World.GetOrCreateSystemManaged<EventLifecycleFixedSystem<TestEvent>>();
            var buffer = GetBuffer();

            var updateReader = buffer.GetUpdateReader(Allocator.Temp);
            var fixedReader = buffer.GetFixedReader(Allocator.Temp);

            var writer = buffer.GetWriter();
            writer.Write(new TestEvent { Value = 1 });
            writer.Write(new TestEvent { Value = 2 });
            writer.Write(new TestEvent { Value = 3 });

            SimulateFixedUpdateFrame<TestEvent>();
            SimulateUpdateFrame<TestEvent>();

            buffer = GetBuffer();
            updateReader.Update(buffer);
            fixedReader.Update(buffer);

            var updateSet = new HashSet<int>();
            var fixedSet = new HashSet<int>();

            foreach (var e in updateReader) updateSet.Add(e.Value);
            foreach (var e in fixedReader) fixedSet.Add(e.Value);

            Assert.AreEqual(3, updateSet.Count, "Update reader should see all 3 events");
            Assert.AreEqual(3, fixedSet.Count, "Fixed reader should see all 3 events");
            Assert.That(updateSet.SetEquals(fixedSet), "Both readers should see same events");

            updateReader.Dispose();
            fixedReader.Dispose();
        }

        #endregion

        #region Lifecycle Tests

        [Test]
        public void Lifecycle_EventsAccessibleAfterSwap()
        {
            World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<TestEvent>>();
            var buffer = GetBuffer();
            var reader = buffer.GetUpdateReader(Allocator.Temp);

            var writer = buffer.GetWriter();
            writer.Write(new TestEvent { Value = 10 });
            writer.Write(new TestEvent { Value = 20 });

            SimulateUpdateFrame<TestEvent>();

            buffer = GetBuffer();
            reader.Update(buffer);

            var readSet = new HashSet<int>();
            foreach (var e in reader) readSet.Add(e.Value);

            Assert.AreEqual(2, readSet.Count, "Reader should still access events after swap");
            Assert.That(readSet.SetEquals(new[] { 10, 20 }));

            reader.Dispose();
        }

        [Test]
        public void Lifecycle_MultipleSwaps_WorksCorrectly()
        {
            var sys = World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<TestEvent>>();

            sys.Update();
            sys.Update();
            sys.Update();

            var buffer = GetBuffer();
            var writer = buffer.GetWriter();
            writer.Write(new TestEvent { Value = 100 });

            var reader = buffer.GetUpdateReader(Allocator.Temp);
            reader.Update(buffer);

            var readSet = new HashSet<int>();
            foreach (var e in reader) readSet.Add(e.Value);

            Assert.AreEqual(1, readSet.Count);
            Assert.That(readSet.Contains(100));

            reader.Dispose();
        }

        #endregion

        #region Edge Cases

        [Test]
        public void EdgeCase_ReaderResumesAfterPause()
        {
            World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<TestEvent>>();
            var buffer = GetBuffer();
            var reader = buffer.GetUpdateReader(Allocator.Temp);

            // Phase 1: Reader active for frames 1-5
            for (int frame = 1; frame <= 5; frame++)
            {
                var writer = buffer.GetWriter();
                writer.Write(new TestEvent { Value = frame });
                SimulateUpdateFrame<TestEvent>();
                buffer = GetBuffer();
                reader.Update(buffer);

                var readSet = new HashSet<int>();
                foreach (var e in reader) readSet.Add(e.Value);
                Assert.That(readSet.Contains(frame), $"Frame {frame}: should read event {frame}");
            }

            // Phase 2: Reader "paused" - write events 6,7,8 and swap them out
            for (int frame = 6; frame <= 8; frame++)
            {
                var writer = buffer.GetWriter();
                writer.Write(new TestEvent { Value = frame });
                SimulateUpdateFrame<TestEvent>();
                buffer = GetBuffer();
            }

            // Phase 3: Write events 9,10 (current frame) without swap yet
            var finalWriter = buffer.GetWriter();
            finalWriter.Write(new TestEvent { Value = 9 });
            finalWriter.Write(new TestEvent { Value = 10 });
            // Note: No swap yet, so 9,10 are in Current, 8 is in Previous

            // Phase 4: Reader resumes - should see Previous(8) + Current(9,10)
            reader.Update(buffer);
            var resumedSet = new HashSet<int>();
            foreach (var e in reader) resumedSet.Add(e.Value);

            // Should see event 8 (in Previous) and 9,10 (in Current)
            Assert.That(resumedSet.Contains(8), "Should see event 8 from Previous buffer");
            Assert.That(resumedSet.Contains(9), "Should see event 9 from Current buffer");
            Assert.That(resumedSet.Contains(10), "Should see event 10 from Current buffer");
            Assert.AreEqual(3, resumedSet.Count, "Should see exactly 3 events (Previous + Current)");

            reader.Dispose();
        }

        [Test]
        public void EdgeCase_LateJoinerAfterLongRun()
        {
            var sys = World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<TestEvent>>();
            var buffer = GetBuffer();

            for (int i = 0; i < 20; i++)
            {
                var writer = buffer.GetWriter();
                writer.Write(new TestEvent { Value = i });
                sys.Update();
                buffer = GetBuffer();
            }

            var lateReader = buffer.GetUpdateReader(Allocator.Temp);
            lateReader.Update(buffer);

            int count = 0;
            foreach (var e in lateReader) count++;

            Assert.GreaterOrEqual(count, 0, "Late reader should work without crash");

            lateReader.Dispose();
        }

        #endregion

        #region Parallel Write Tests

        [Test]
        public void ParallelWrite_SingleJob_EventsCommitted()
        {
            World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<ParallelTestEvent>>();
            var buffer = m_Manager.CreateEntityQuery(typeof(EventBuffer<ParallelTestEvent>))
                .GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            const int itemCount = 160;
            // NativeStream requires unique access per index. For IJobParallelFor doing 1 item per index,
            // we must allocate a stream slot for each item (1-to-1 mapping).
            var writerHandle = buffer.GetParallelWriter(itemCount, Allocator.TempJob);

            var job = new TestParallelWriteJob
            {
                Writer = writerHandle.Writer
            };

            var dependency = job.Schedule(itemCount, 1);
            writerHandle.ScheduleCommit(buffer, ref dependency);
            dependency.Complete();

            var resultBuffer = m_Manager.CreateEntityQuery(typeof(EventBuffer<ParallelTestEvent>))
                .GetSingleton<EventBuffer<ParallelTestEvent>>();

            Assert.AreEqual(itemCount, resultBuffer.BufferUpdateCurrent.Length,
                "Should have written all items");
            Assert.AreEqual(itemCount, resultBuffer.BufferFixedCurrent.Length,
                "Should have written events to both buffers");
        }

        [Test]
        public void ParallelWrite_NoEvents_NoException()
        {
            World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<ParallelTestEvent>>();
            var buffer = m_Manager.CreateEntityQuery(typeof(EventBuffer<ParallelTestEvent>))
                .GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            const int itemCount = 10;
            var writerHandle = buffer.GetParallelWriter(itemCount, Allocator.TempJob);

            var job = new TestNoOpJob
            {
                Writer = writerHandle.Writer
            };

            var dependency = job.Schedule(itemCount, 1);
            writerHandle.ScheduleCommit(buffer, ref dependency);

            Assert.DoesNotThrow(() => dependency.Complete());

            var resultBuffer = m_Manager.CreateEntityQuery(typeof(EventBuffer<ParallelTestEvent>))
                .GetSingleton<EventBuffer<ParallelTestEvent>>();

            Assert.AreEqual(0, resultBuffer.BufferUpdateCurrent.Length);
        }

        [Test]
        public void ParallelWrite_MultipleJobsInSameSystem_EventsCommitted()
        {
            World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<ParallelTestEvent>>();

            var sys = World.GetOrCreateSystemManaged<TestMultiJobSystem>();
            sys.ItemCount1 = 50;
            sys.ItemCount2 = 50;
            sys.Update();
            m_Manager.CompleteAllTrackedJobs();

            var resultBuffer = m_Manager.CreateEntityQuery(typeof(EventBuffer<ParallelTestEvent>))
                .GetSingleton<EventBuffer<ParallelTestEvent>>();

            Assert.AreEqual(100, resultBuffer.BufferUpdateCurrent.Length,
                "Should have 100 events from both jobs in same system");
        }

        [Test]
        public void ParallelWrite_MultipleSystemsWithAutoDependency_EventsCommitted()
        {
            World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<ParallelTestEvent>>();

            var sys1 = World.GetOrCreateSystemManaged<TestParallelSystem1>();
            var sys2 = World.GetOrCreateSystemManaged<TestParallelSystem2>();

            sys1.ItemCount = 50;
            sys2.ItemCount = 50;

            sys1.Update();
            sys2.Update();
            m_Manager.CompleteAllTrackedJobs();

            var resultBuffer = m_Manager.CreateEntityQuery(typeof(EventBuffer<ParallelTestEvent>))
                .GetSingleton<EventBuffer<ParallelTestEvent>>();

            Assert.AreEqual(100, resultBuffer.BufferUpdateCurrent.Length,
                "Should have 100 events from two systems with auto dependency management");
        }

        [Test]
        public void ParallelWrite_LargeEventCount_CapacityExpands()
        {
            World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<ParallelTestEvent>>();
            var buffer = m_Manager.CreateEntityQuery(typeof(EventBuffer<ParallelTestEvent>))
                .GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            const int streamIndexCount = 64; // Number of unique stream indices (and parallel jobs)
            const int itemsPerIndex = 300;
            const int totalItems = streamIndexCount * itemsPerIndex; // 19200 items

            // NativeStream.forEachCount must match the number of unique indices we write to
            var writerHandle = buffer.GetParallelWriter(streamIndexCount, Allocator.TempJob);

            var job = new TestParallelBatchedWriteJob
            {
                Writer = writerHandle.Writer,
                ItemsPerBatch = itemsPerIndex
            };

            // Schedule(arrayLength, innerLoopBatchCount)
            // arrayLength must match streamIndexCount for 1-to-1 mapping
            // innerLoopBatchCount = 1 to encourage maximum parallelism for this test
            var dependency = job.Schedule(streamIndexCount, 1);
            writerHandle.ScheduleCommit(buffer, ref dependency);
            dependency.Complete();

            var resultBuffer = m_Manager.CreateEntityQuery(typeof(EventBuffer<ParallelTestEvent>))
                .GetSingleton<EventBuffer<ParallelTestEvent>>();

            Assert.AreEqual(totalItems, resultBuffer.BufferUpdateCurrent.Length);
            Assert.GreaterOrEqual(resultBuffer.BufferUpdateCurrent.Capacity, totalItems,
                "Capacity should have expanded");

            var capacity = resultBuffer.BufferUpdateCurrent.Capacity;
            Assert.IsTrue(IsPowerOfTwo(capacity), $"Capacity {capacity} should be power of two");
        }

        private bool IsPowerOfTwo(int value)
        {
            return value > 0 && (value & (value - 1)) == 0;
        }

        #endregion

        #region Test Helper Jobs

        [BurstCompile]
        struct TestParallelWriteJob : IJobParallelFor
        {
            public ParallelEventWriter<ParallelTestEvent> Writer;

            public void Execute(int index)
            {
                // Use index directly (1-to-1 mapping)
                Writer.BeginForEachIndex(index);
                Writer.Write(new ParallelTestEvent
                {
                    ThreadIndex = 0, // No longer tracking thread index
                    Value = index
                });
                Writer.EndForEachIndex();
            }
        }

        [BurstCompile]
        struct TestNoOpJob : IJobParallelFor
        {
            public ParallelEventWriter<ParallelTestEvent> Writer;

            public void Execute(int index)
            {
                Writer.BeginForEachIndex(index);
                // Write nothing
                Writer.EndForEachIndex();
            }
        }

        [BurstCompile]
        struct TestParallelWriteJobWithOffset : IJobParallelFor
        {
            [NativeDisableContainerSafetyRestriction]
            public ParallelEventWriter<ParallelTestEvent> Writer;
            public int ItemOffset;

            public void Execute(int index)
            {
                // Map the job index (0..count-1) to the global stream index
                int streamIndex = ItemOffset + index;

                Writer.BeginForEachIndex(streamIndex);
                Writer.Write(new ParallelTestEvent
                {
                    ThreadIndex = 0,
                    Value = streamIndex
                });
                Writer.EndForEachIndex();
            }
        }

        [BurstCompile]
        struct TestParallelBatchedWriteJob : IJobParallelFor
        {
            public ParallelEventWriter<ParallelTestEvent> Writer;
            public int ItemsPerBatch;

            public void Execute(int index)
            {
                // Each index represents a batch (a stream slot).
                // We write multiple items into this single slot.
                Writer.BeginForEachIndex(index);
                for (int i = 0; i < ItemsPerBatch; i++)
                {
                    Writer.Write(new ParallelTestEvent
                    {
                        ThreadIndex = index,
                        Value = (index * ItemsPerBatch) + i
                    });
                }
                Writer.EndForEachIndex();
            }
        }

        partial class TestCommitSystem : SystemBase
        {
            protected override void OnUpdate() { }
        }

        partial class TestMultiJobSystem : SystemBase
        {
            public int ItemCount1;
            public int ItemCount2;

            protected override void OnUpdate()
            {
                var buffer = SystemAPI.GetSingletonRW<EventBuffer<ParallelTestEvent>>();

                // Allocate enough slots for both jobs
                var writerHandle = buffer.GetParallelWriter(ItemCount1 + ItemCount2, Allocator.TempJob);

                var job1 = new TestParallelWriteJobWithOffset { Writer = writerHandle.Writer, ItemOffset = 0 };
                var job2 = new TestParallelWriteJobWithOffset { Writer = writerHandle.Writer, ItemOffset = ItemCount1 };

                var dep = Dependency;
                dep = job1.Schedule(ItemCount1, 1, dep);
                dep = job2.Schedule(ItemCount2, 1, dep);
                writerHandle.ScheduleCommit(buffer, ref dep);
                Dependency = dep;
            }
        }

        partial class TestParallelSystem1 : SystemBase
        {
            public int ItemCount;

            protected override void OnUpdate()
            {
                var buffer = SystemAPI.GetSingletonRW<EventBuffer<ParallelTestEvent>>();
                var writerHandle = buffer.GetParallelWriter(ItemCount, Allocator.TempJob);

                var job = new TestParallelWriteJob { Writer = writerHandle.Writer };
                var dep = Dependency;
                dep = job.Schedule(ItemCount, 1, dep);
                writerHandle.ScheduleCommit(buffer, ref dep);
                Dependency = dep;
            }
        }

        partial class TestParallelSystem2 : SystemBase
        {
            public int ItemCount;

            protected override void OnUpdate()
            {
                var buffer = SystemAPI.GetSingletonRW<EventBuffer<ParallelTestEvent>>();
                // Ensure unique stream indices by allocating strict count
                var writerHandle = buffer.GetParallelWriter(ItemCount, Allocator.TempJob);

                var job = new TestParallelWriteJob { Writer = writerHandle.Writer };
                var dep = Dependency;
                dep = job.Schedule(ItemCount, 1, dep);
                writerHandle.ScheduleCommit(buffer, ref dep);
                Dependency = dep;
            }
        }

        #endregion
    }
}

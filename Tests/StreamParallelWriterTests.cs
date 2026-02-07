using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.Tests;
using Unity.Jobs;
using IceEvents;
using System;
using Unity.Collections.LowLevel.Unsafe;

[assembly: RegisterGenericComponentType(typeof(EventBuffer<IceEvents.Tests.ParallelTestEvent>))]
[assembly: RegisterGenericJobType(typeof(EventCommitJob<IceEvents.Tests.ParallelTestEvent>))]
[assembly: RegisterGenericSystemType(typeof(EventLifecycleUpdateSystem<IceEvents.Tests.ParallelTestEvent>))]
[assembly: RegisterGenericSystemType(typeof(EventLifecycleFixedSystem<IceEvents.Tests.ParallelTestEvent>))]

namespace IceEvents.Tests
{
    // ParallelTestEvent and StreamParallelWriteConfig replaced by ParallelTestEvent and ParallelWriteConfig in ParallelWriteTestCommon.cs

    #region Helper Jobs

    [BurstCompile]
    struct StreamParallelWriteJob : IJobParallelFor
    {
        public StreamParallelEventWriter<ParallelTestEvent> Writer;

        public void Execute(int index)
        {
            Writer.BeginForEachIndex(index);
            Writer.Write(new ParallelTestEvent
            {
                ThreadIndex = 0,
                Value = index
            });
            Writer.EndForEachIndex();
        }
    }

    [BurstCompile]
    struct StreamParallelNoOpJob : IJobParallelFor
    {
        public StreamParallelEventWriter<ParallelTestEvent> Writer;

        public void Execute(int index)
        {
            Writer.BeginForEachIndex(index);
            // Write nothing
            Writer.EndForEachIndex();
        }
    }

    [BurstCompile]
    struct StreamParallelOffsetWriteJob : IJobParallelFor
    {
        [NativeDisableContainerSafetyRestriction]
        public StreamParallelEventWriter<ParallelTestEvent> Writer;
        public int ItemOffset;

        public void Execute(int index)
        {
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
    struct StreamParallelBatchedWriteJob : IJobParallelFor
    {
        public StreamParallelEventWriter<ParallelTestEvent> Writer;
        public int ItemsPerBatch;

        public void Execute(int index)
        {
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

    // For capacity test (was TestNoOpJobWithWrite)
    [BurstCompile]
    struct StreamParallelCapacityTestJob : IJobParallelFor
    {
        public StreamParallelEventWriter<ParallelTestEvent> Writer;
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

    #region Test Systems

    [DisableAutoCreation]
    partial struct StreamParallelSingleJobWriteSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var config = SystemAPI.GetSingleton<ParallelWriteConfig>();
            var buffer = SystemAPI.GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            var writerHandle = buffer.ValueRW.GetStreamParallelWriter(config.ItemCount, Allocator.TempJob);

            var job = new StreamParallelWriteJob { Writer = writerHandle.Writer };
            state.Dependency = job.Schedule(config.ItemCount, 1, state.Dependency);
            writerHandle.ScheduleCommit(ref state);
        }
    }

    [DisableAutoCreation]
    partial struct StreamParallelNoOpWriteSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var config = SystemAPI.GetSingleton<ParallelWriteConfig>();
            var buffer = SystemAPI.GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            var writerHandle = buffer.ValueRW.GetStreamParallelWriter(config.ItemCount, Allocator.TempJob);

            var job = new StreamParallelNoOpJob { Writer = writerHandle.Writer };
            state.Dependency = job.Schedule(config.ItemCount, 1, state.Dependency);
            writerHandle.ScheduleCommit(ref state);
        }
    }

    [DisableAutoCreation]
    partial struct StreamParallelMultiJobWriteSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var config = SystemAPI.GetSingleton<ParallelWriteConfig>();
            var buffer = SystemAPI.GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            // ItemCount serves as Count1, ItemOffset serves as Count2 here (a bit of reuse)
            int count1 = config.ItemCount;
            int count2 = config.ItemOffset; // Reusing field for second count

            var writerHandle = buffer.ValueRW.GetStreamParallelWriter(count1 + count2, Allocator.TempJob);

            var job1 = new StreamParallelOffsetWriteJob { Writer = writerHandle.Writer, ItemOffset = 0 };
            var job2 = new StreamParallelOffsetWriteJob { Writer = writerHandle.Writer, ItemOffset = count1 };

            state.Dependency = job1.Schedule(count1, 1, state.Dependency);
            state.Dependency = job2.Schedule(count2, 1, state.Dependency);
            writerHandle.ScheduleCommit(ref state);
        }
    }

    [DisableAutoCreation]
    partial struct StreamParallelSystemA : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var config = SystemAPI.GetSingleton<ParallelWriteConfig>();
            var buffer = SystemAPI.GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            var writerHandle = buffer.ValueRW.GetStreamParallelWriter(config.ItemCount, Allocator.TempJob);
            var job = new StreamParallelWriteJob { Writer = writerHandle.Writer };
            state.Dependency = job.Schedule(config.ItemCount, 1, state.Dependency);
            writerHandle.ScheduleCommit(ref state);
        }
    }

    [DisableAutoCreation]
    partial struct StreamParallelSystemB : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var config = SystemAPI.GetSingleton<ParallelWriteConfig>();
            var buffer = SystemAPI.GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            // Typically SystemB might write different content, here we just use same logic but it runs after A due to RW dependency
            var writerHandle = buffer.ValueRW.GetStreamParallelWriter(config.ItemCount, Allocator.TempJob);
            var job = new StreamParallelWriteJob { Writer = writerHandle.Writer };
            state.Dependency = job.Schedule(config.ItemCount, 1, state.Dependency);
            writerHandle.ScheduleCommit(ref state);
        }
    }

    [DisableAutoCreation]
    partial struct StreamParallelBatchedWriteSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var config = SystemAPI.GetSingleton<ParallelWriteConfig>();
            var buffer = SystemAPI.GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            // unique indices count
            int streamIndexCount = config.ItemCount;

            var writerHandle = buffer.ValueRW.GetStreamParallelWriter(streamIndexCount, Allocator.TempJob);

            var job = new StreamParallelBatchedWriteJob
            {
                Writer = writerHandle.Writer,
                ItemsPerBatch = config.ItemsPerBatch
            };

            state.Dependency = job.Schedule(streamIndexCount, 1, state.Dependency);
            writerHandle.ScheduleCommit(ref state);
        }
    }

    [DisableAutoCreation]
    partial struct StreamParallelCapacityTestSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var config = SystemAPI.GetSingleton<ParallelWriteConfig>();
            var buffer = SystemAPI.GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            if (config.InitialCapacity > 0)
            {
                buffer.ValueRW.BufferUpdateCurrent.SetCapacity(config.InitialCapacity);
            }

            var writerHandle = buffer.ValueRW.GetStreamParallelWriter(config.ItemCount, Allocator.TempJob);

            var job = new StreamParallelCapacityTestJob
            {
                Writer = writerHandle.Writer,
                ItemCount = config.ItemCount
            };

            state.Dependency = job.Schedule(config.ItemCount, 1, state.Dependency);
            writerHandle.ScheduleCommit(ref state);
        }
    }

    #endregion

    [DisableAutoCreation]
    partial struct StreamParallelDoubleCommitSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var config = SystemAPI.GetSingleton<ParallelWriteConfig>();
            var buffer = SystemAPI.GetSingletonRW<EventBuffer<ParallelTestEvent>>();
            var writerHandle = buffer.ValueRW.GetStreamParallelWriter(config.ItemCount, Allocator.TempJob);

            // Commit once
            writerHandle.ScheduleCommit(ref state);

            // Commit twice
            writerHandle.ScheduleCommit(ref state);
        }
    }

    [TestFixture]
    public partial class StreamParallelWriterTests : ECSTestsFixture
    {
        [SetUp]
        public override void Setup()
        {
            base.Setup();
            // Ensure lifecycle system is created
            World.GetOrCreateSystem<EventLifecycleUpdateSystem<ParallelTestEvent>>();
        }

        private EventBuffer<ParallelTestEvent> GetBuffer()
        {
            var query = m_Manager.CreateEntityQuery(typeof(EventBuffer<ParallelTestEvent>));
            return m_Manager.GetComponentData<EventBuffer<ParallelTestEvent>>(query.GetSingletonEntity());
        }

        #region Functional Tests

        [Test]
        public void StreamParallelWrite_SingleJob_EventsCommitted()
        {
            // Prepare
            var configEntity = m_Manager.CreateEntity(typeof(ParallelWriteConfig));
            m_Manager.SetComponentData(configEntity, new ParallelWriteConfig { ItemCount = 160 });

            // Execute
            var sys = World.CreateSystem<StreamParallelSingleJobWriteSystem>();
            sys.Update(World.Unmanaged);

            // Assert
            var buffer = GetBuffer();
            Assert.AreEqual(160, buffer.BufferUpdateCurrent.Length, "Should have written all items");
            Assert.AreEqual(160, buffer.BufferFixedCurrent.Length, "Should have written events to both buffers");
        }

        [Test]
        public void StreamParallelWrite_NoEvents_NoException()
        {
            // Prepare
            var configEntity = m_Manager.CreateEntity(typeof(ParallelWriteConfig));
            m_Manager.SetComponentData(configEntity, new ParallelWriteConfig { ItemCount = 10 });

            // Execute
            var sys = World.CreateSystem<StreamParallelNoOpWriteSystem>();
            Assert.DoesNotThrow(() => sys.Update(World.Unmanaged));

            // Assert
            var buffer = GetBuffer();
            Assert.AreEqual(0, buffer.BufferUpdateCurrent.Length, "Should accept empty writes");
        }

        [Test]
        public void StreamParallelWrite_MultipleJobsInSameSystem_EventsCommitted()
        {
            // Prepare
            var configEntity = m_Manager.CreateEntity(typeof(ParallelWriteConfig));
            m_Manager.SetComponentData(configEntity, new ParallelWriteConfig
            {
                ItemCount = 50, // Count1
                ItemOffset = 50 // Count2 (reused field)
            });

            // Execute
            var sys = World.CreateSystem<StreamParallelMultiJobWriteSystem>();
            sys.Update(World.Unmanaged);
            m_Manager.CompleteAllTrackedJobs();

            // Assert
            var buffer = GetBuffer();
            Assert.AreEqual(100, buffer.BufferUpdateCurrent.Length, "Should have 100 events from both jobs");
        }

        [Test]
        public void StreamParallelWrite_MultipleSystemsWithAutoDependency_EventsCommitted()
        {
            // Prepare
            var configEntity = m_Manager.CreateEntity(typeof(ParallelWriteConfig));
            m_Manager.SetComponentData(configEntity, new ParallelWriteConfig { ItemCount = 50 });

            // Execute
            var sys1 = World.CreateSystem<StreamParallelSystemA>();
            var sys2 = World.CreateSystem<StreamParallelSystemB>();

            sys1.Update(World.Unmanaged);
            sys2.Update(World.Unmanaged); // Should auto-depend on sys1
            m_Manager.CompleteAllTrackedJobs();

            // Assert
            var buffer = GetBuffer();
            Assert.AreEqual(100, buffer.BufferUpdateCurrent.Length, "Should have 100 events from two systems with auto dependency");
        }

        [Test]
        public void StreamParallelWrite_LargeEventCount_CapacityExpands()
        {
            // Prepare
            int streamIndexCount = 64;
            int itemsPerIndex = 300;
            int totalItems = streamIndexCount * itemsPerIndex; // 19200

            var configEntity = m_Manager.CreateEntity(typeof(ParallelWriteConfig));
            m_Manager.SetComponentData(configEntity, new ParallelWriteConfig
            {
                ItemCount = streamIndexCount,
                ItemsPerBatch = itemsPerIndex
            });

            // Execute
            var sys = World.CreateSystem<StreamParallelBatchedWriteSystem>();
            sys.Update(World.Unmanaged);
            m_Manager.CompleteAllTrackedJobs(); // Ensure completion for assertions

            // Assert
            var buffer = GetBuffer();
            Assert.AreEqual(totalItems, buffer.BufferUpdateCurrent.Length);
            Assert.GreaterOrEqual(buffer.BufferUpdateCurrent.Capacity, totalItems, "Capacity should have expanded");

            var capacity = buffer.BufferUpdateCurrent.Capacity;
            Assert.IsTrue((capacity & (capacity - 1)) == 0, $"Capacity {capacity} should be power of two");
        }

        [Test]
        public void StreamParallelWrite_CapacitySufficient_NoExpansion()
        {
            // Prepare
            int initialCapacity = 2048;
            int writeCount = 100;

            var configEntity = m_Manager.CreateEntity(typeof(ParallelWriteConfig));
            m_Manager.SetComponentData(configEntity, new ParallelWriteConfig
            {
                ItemCount = writeCount,
                InitialCapacity = initialCapacity
            });

            // Execute
            var sys = World.CreateSystem<StreamParallelCapacityTestSystem>();
            sys.Update(World.Unmanaged);
            m_Manager.CompleteAllTrackedJobs();

            // Assert
            var buffer = GetBuffer();
            Assert.AreEqual(writeCount, buffer.BufferUpdateCurrent.Length);
            Assert.AreEqual(initialCapacity, buffer.BufferUpdateCurrent.Capacity, "Capacity should not change if sufficient");
        }

        [Test]
        public void StreamParallelWrite_DoubleCommit_DoesNotThrow()
        {
            var configEntity = m_Manager.CreateEntity(typeof(ParallelWriteConfig));
            m_Manager.SetComponentData(configEntity, new ParallelWriteConfig { ItemCount = 10 });

            var sys = World.CreateSystem<StreamParallelDoubleCommitSystem>();
            Assert.DoesNotThrow(() => sys.Update(World.Unmanaged));
            m_Manager.CompleteAllTrackedJobs();
        }

        #endregion

        #region Boundary Tests

        [Test]
        public void StreamParallelWrite_ZeroThreadCount_Throws()
        {
            var sys = World.GetOrCreateSystem<EventLifecycleUpdateSystem<ParallelTestEvent>>();
            var buffer = m_Manager.CreateEntityQuery(typeof(EventBuffer<ParallelTestEvent>))
                .GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            Assert.Throws<ArgumentException>(() =>
                buffer.ValueRW.GetStreamParallelWriter(0, Allocator.TempJob),
                "Should throw ArgumentException when threadCount is 0");
        }

        [Test]
        public void StreamParallelWrite_LargeThreadIndex_Handles()
        {
            var sys = World.GetOrCreateSystem<EventLifecycleUpdateSystem<ParallelTestEvent>>();
            var buffer = m_Manager.CreateEntityQuery(typeof(EventBuffer<ParallelTestEvent>))
                .GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            int declaredThreadCount = 10;
            var writerHandle = buffer.ValueRW.GetStreamParallelWriter(declaredThreadCount, Allocator.TempJob);
            var writer = writerHandle.Writer;

            Assert.Throws<ArgumentException>(() =>
            {
                writer.BeginForEachIndex(declaredThreadCount); // Index 10 is out of bounds [0..9]
            }, "Should throw when accessing index >= declared count");

            writerHandle.Dispose();
        }
        #endregion
    }
}

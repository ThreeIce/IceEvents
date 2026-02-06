using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.Tests;
using Unity.Jobs;
using IceEvents;
using System;
using Unity.Collections.LowLevel.Unsafe;

namespace IceEvents.Tests
{
    // Note: Assembly attributes for ParallelTestEvent are already in StreamParallelWriterTests.cs (or Common), 
    // but duplication is harmless or we can move them to Common.
    // For now we assume they are registered.

    #region Helper Jobs for Queue

    [BurstCompile]
    struct QueueParallelWriteJob : IJobParallelFor
    {
        public QueueParallelEventWriter<ParallelTestEvent> Writer;

        public void Execute(int index)
        {
            // No BeginForEachIndex needed!
            Writer.Write(new ParallelTestEvent
            {
                ThreadIndex = 0,
                Value = index
            });
        }
    }

    [BurstCompile]
    struct QueueParallelNoOpJob : IJobParallelFor
    {
        public QueueParallelEventWriter<ParallelTestEvent> Writer;

        public void Execute(int index)
        {
            // Do nothing
        }
    }

    [BurstCompile]
    struct QueueParallelMultiJobWriteJob : IJobParallelFor
    {
        public QueueParallelEventWriter<ParallelTestEvent> Writer;
        public int ItemOffset;

        public void Execute(int index)
        {
            int wrapperIndex = ItemOffset + index;
            Writer.Write(new ParallelTestEvent
            {
                ThreadIndex = 0,
                Value = wrapperIndex
            });
        }
    }

    [BurstCompile]
    struct QueueParallelBatchedWriteJob : IJobParallelFor
    {
        public QueueParallelEventWriter<ParallelTestEvent> Writer;
        public int ItemsPerBatch;

        public void Execute(int index)
        {
            // index here is "stream index" concept from test config, but for Queue it's just a job index
            // We simulate writing multiple items per job index
            for (int i = 0; i < ItemsPerBatch; i++)
            {
                Writer.Write(new ParallelTestEvent
                {
                    ThreadIndex = index,
                    Value = (index * ItemsPerBatch) + i
                });
            }
        }
    }

    [BurstCompile]
    struct QueueParallelCapacityTestJob : IJobParallelFor
    {
        public QueueParallelEventWriter<ParallelTestEvent> Writer;
        public int ItemCount;

        public void Execute(int index)
        {
            if (index < ItemCount)
            {
                Writer.Write(new ParallelTestEvent { Value = index });
            }
        }
    }

    #endregion

    #region Test Systems for Queue

    [DisableAutoCreation]
    partial struct QueueParallelSingleJobWriteSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var config = SystemAPI.GetSingleton<ParallelWriteConfig>();
            var buffer = SystemAPI.GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            var writerHandle = buffer.ValueRW.GetQueueParallelWriter(Allocator.TempJob);

            var job = new QueueParallelWriteJob { Writer = writerHandle.Writer };
            state.Dependency = job.Schedule(config.ItemCount, 64, state.Dependency);
            writerHandle.ScheduleCommit(ref state);
        }
    }

    [DisableAutoCreation]
    partial struct QueueParallelNoOpWriteSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var config = SystemAPI.GetSingleton<ParallelWriteConfig>();
            var buffer = SystemAPI.GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            var writerHandle = buffer.ValueRW.GetQueueParallelWriter(Allocator.TempJob);

            var job = new QueueParallelNoOpJob { Writer = writerHandle.Writer };
            state.Dependency = job.Schedule(config.ItemCount, 64, state.Dependency);
            writerHandle.ScheduleCommit(ref state);
        }
    }

    [DisableAutoCreation]
    partial struct QueueParallelMultiJobWriteSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var config = SystemAPI.GetSingleton<ParallelWriteConfig>();
            var buffer = SystemAPI.GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            int count1 = config.ItemCount;
            int count2 = config.ItemOffset;

            var writerHandle = buffer.ValueRW.GetQueueParallelWriter(Allocator.TempJob);

            var job1 = new QueueParallelMultiJobWriteJob { Writer = writerHandle.Writer, ItemOffset = 0 };
            var job2 = new QueueParallelMultiJobWriteJob { Writer = writerHandle.Writer, ItemOffset = count1 };

            state.Dependency = job1.Schedule(count1, 64, state.Dependency);
            state.Dependency = job2.Schedule(count2, 64, state.Dependency);
            writerHandle.ScheduleCommit(ref state);
        }
    }

    [DisableAutoCreation]
    partial struct QueueParallelSystemA : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var config = SystemAPI.GetSingleton<ParallelWriteConfig>();
            var buffer = SystemAPI.GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            var writerHandle = buffer.ValueRW.GetQueueParallelWriter(Allocator.TempJob);
            var job = new QueueParallelWriteJob { Writer = writerHandle.Writer };
            state.Dependency = job.Schedule(config.ItemCount, 64, state.Dependency);
            writerHandle.ScheduleCommit(ref state);
        }
    }

    [DisableAutoCreation]
    partial struct QueueParallelSystemB : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var config = SystemAPI.GetSingleton<ParallelWriteConfig>();
            var buffer = SystemAPI.GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            var writerHandle = buffer.ValueRW.GetQueueParallelWriter(Allocator.TempJob);
            var job = new QueueParallelWriteJob { Writer = writerHandle.Writer };
            state.Dependency = job.Schedule(config.ItemCount, 64, state.Dependency);
            writerHandle.ScheduleCommit(ref state);
        }
    }

    [DisableAutoCreation]
    partial struct QueueParallelBatchedWriteSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var config = SystemAPI.GetSingleton<ParallelWriteConfig>();
            var buffer = SystemAPI.GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            int streamIndexCount = config.ItemCount;

            var writerHandle = buffer.ValueRW.GetQueueParallelWriter(Allocator.TempJob);

            var job = new QueueParallelBatchedWriteJob
            {
                Writer = writerHandle.Writer,
                ItemsPerBatch = config.ItemsPerBatch
            };

            state.Dependency = job.Schedule(streamIndexCount, 64, state.Dependency);
            writerHandle.ScheduleCommit(ref state);
        }
    }

    [DisableAutoCreation]
    partial struct QueueParallelCapacityTestSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var config = SystemAPI.GetSingleton<ParallelWriteConfig>();
            var buffer = SystemAPI.GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            if (config.InitialCapacity > 0)
            {
                buffer.ValueRW.BufferUpdateCurrent.SetCapacity(config.InitialCapacity);
            }

            var writerHandle = buffer.ValueRW.GetQueueParallelWriter(Allocator.TempJob);

            var job = new QueueParallelCapacityTestJob
            {
                Writer = writerHandle.Writer,
                ItemCount = config.ItemCount
            };

            state.Dependency = job.Schedule(config.ItemCount, 64, state.Dependency);
            writerHandle.ScheduleCommit(ref state);
        }
    }

    #endregion

    [TestFixture]
    public partial class QueueParallelWriterTests : ECSTestsFixture
    {
        [SetUp]
        public override void Setup()
        {
            base.Setup();
            World.GetOrCreateSystem<EventLifecycleUpdateSystem<ParallelTestEvent>>();
        }

        private EventBuffer<ParallelTestEvent> GetBuffer()
        {
            var query = m_Manager.CreateEntityQuery(typeof(EventBuffer<ParallelTestEvent>));
            return m_Manager.GetComponentData<EventBuffer<ParallelTestEvent>>(query.GetSingletonEntity());
        }

        #region Functional Tests

        [Test]
        public void QueueParallelWrite_SingleJob_EventsCommitted()
        {
            var configEntity = m_Manager.CreateEntity(typeof(ParallelWriteConfig));
            m_Manager.SetComponentData(configEntity, new ParallelWriteConfig { ItemCount = 160 });

            var sys = World.CreateSystem<QueueParallelSingleJobWriteSystem>();
            sys.Update(World.Unmanaged);

            var buffer = GetBuffer();
            Assert.AreEqual(160, buffer.BufferUpdateCurrent.Length, "Should have written all items");
            Assert.AreEqual(160, buffer.BufferFixedCurrent.Length);
        }

        [Test]
        public void QueueParallelWrite_NoEvents_NoException()
        {
            var configEntity = m_Manager.CreateEntity(typeof(ParallelWriteConfig));
            m_Manager.SetComponentData(configEntity, new ParallelWriteConfig { ItemCount = 10 });

            var sys = World.CreateSystem<QueueParallelNoOpWriteSystem>();
            Assert.DoesNotThrow(() => sys.Update(World.Unmanaged));

            var buffer = GetBuffer();
            Assert.AreEqual(0, buffer.BufferUpdateCurrent.Length);
        }

        [Test]
        public void QueueParallelWrite_MultipleJobsInSameSystem_EventsCommitted()
        {
            var configEntity = m_Manager.CreateEntity(typeof(ParallelWriteConfig));
            m_Manager.SetComponentData(configEntity, new ParallelWriteConfig
            {
                ItemCount = 50,
                ItemOffset = 50
            });

            var sys = World.CreateSystem<QueueParallelMultiJobWriteSystem>();
            sys.Update(World.Unmanaged);
            m_Manager.CompleteAllTrackedJobs();

            var buffer = GetBuffer();
            Assert.AreEqual(100, buffer.BufferUpdateCurrent.Length);
        }

        [Test]
        public void QueueParallelWrite_MultipleSystemsWithAutoDependency_EventsCommitted()
        {
            var configEntity = m_Manager.CreateEntity(typeof(ParallelWriteConfig));
            m_Manager.SetComponentData(configEntity, new ParallelWriteConfig { ItemCount = 50 });

            var sys1 = World.CreateSystem<QueueParallelSystemA>();
            var sys2 = World.CreateSystem<QueueParallelSystemB>();

            sys1.Update(World.Unmanaged);
            sys2.Update(World.Unmanaged);
            m_Manager.CompleteAllTrackedJobs();

            var buffer = GetBuffer();
            Assert.AreEqual(100, buffer.BufferUpdateCurrent.Length);
        }

        [Test]
        public void QueueParallelWrite_LargeEventCount_CapacityExpands()
        {
            int streamIndexCount = 64;
            int itemsPerIndex = 300;
            int totalItems = streamIndexCount * itemsPerIndex;

            var configEntity = m_Manager.CreateEntity(typeof(ParallelWriteConfig));
            m_Manager.SetComponentData(configEntity, new ParallelWriteConfig
            {
                ItemCount = streamIndexCount,
                ItemsPerBatch = itemsPerIndex
            });

            var sys = World.CreateSystem<QueueParallelBatchedWriteSystem>();
            sys.Update(World.Unmanaged);
            m_Manager.CompleteAllTrackedJobs();

            var buffer = GetBuffer();
            Assert.AreEqual(totalItems, buffer.BufferUpdateCurrent.Length);
        }

        [Test]
        public void QueueParallelWrite_CapacitySufficient_NoExpansion()
        {
            int initialCapacity = 2048;
            int writeCount = 100;

            var configEntity = m_Manager.CreateEntity(typeof(ParallelWriteConfig));
            m_Manager.SetComponentData(configEntity, new ParallelWriteConfig
            {
                ItemCount = writeCount,
                InitialCapacity = initialCapacity
            });

            var sys = World.CreateSystem<QueueParallelCapacityTestSystem>();
            sys.Update(World.Unmanaged);
            m_Manager.CompleteAllTrackedJobs();

            var buffer = GetBuffer();
            Assert.AreEqual(writeCount, buffer.BufferUpdateCurrent.Length);
            Assert.AreEqual(initialCapacity, buffer.BufferUpdateCurrent.Capacity);
        }
        #endregion
    }
}

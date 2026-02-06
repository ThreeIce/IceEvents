using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.Tests;
using Unity.Jobs;
using IceEvents;
using System.Diagnostics;
using Unity.PerformanceTesting;

namespace IceEvents.Tests
{
    // Reuse StreamParallelTestEvent and StreamParallelWriteConfig from StreamParallelWriterTests.cs

    [DisableAutoCreation]
    partial struct StreamParallelStressWriteSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var config = SystemAPI.GetSingleton<StreamParallelWriteConfig>();
            var buffer = SystemAPI.GetSingletonRW<EventBuffer<StreamParallelTestEvent>>();

            // Check if we need to force capacity (for capacity growth test)
            if (config.InitialCapacity > 0)
            {
                buffer.ValueRW.BufferUpdateCurrent.Clear(); // Clear existing
                buffer.ValueRW.BufferUpdateCurrent.SetCapacity(config.InitialCapacity);
            }

            int batchCount = config.ItemCount; // Treated as batch/thread count

            // Allocate parallel writer
            var writerHandle = buffer.ValueRW.GetStreamParallelWriter(batchCount, Allocator.TempJob);

            // Depending on ItemsPerBatch, choose job
            if (config.ItemsPerBatch > 1)
            {
                var job = new StressTestJob
                {
                    Writer = writerHandle.Writer,
                    ItemsPerBatch = config.ItemsPerBatch,
                    TotalLimit = 1000000 // Just a high limit, or we could pass it in config
                };
                // For performance tests, we usually run with some batch size for inner loop
                state.Dependency = job.Schedule(batchCount, 32, state.Dependency);
            }
            else
            {
                var job = new StressTestSingleItemJob
                {
                    Writer = writerHandle.Writer
                };
                state.Dependency = job.Schedule(batchCount, 64, state.Dependency);
            }

            writerHandle.ScheduleCommit(ref state);
        }
    }

    [BurstCompile]
    struct StressTestJob : IJobParallelFor
    {
        public StreamParallelEventWriter<StreamParallelTestEvent> Writer;
        public int ItemsPerBatch;
        public int TotalLimit;

        public void Execute(int index)
        {
            Writer.BeginForEachIndex(index);
            int baseVal = index * ItemsPerBatch;
            for (int i = 0; i < ItemsPerBatch; i++)
            {
                // Simple write loop
                Writer.Write(new StreamParallelTestEvent { Value = baseVal + i });
            }
            Writer.EndForEachIndex();
        }
    }

    [BurstCompile]
    struct StressTestSingleItemJob : IJobParallelFor
    {
        public StreamParallelEventWriter<StreamParallelTestEvent> Writer;
        public void Execute(int index)
        {
            Writer.BeginForEachIndex(index);
            Writer.Write(new StreamParallelTestEvent { Value = index });
            Writer.EndForEachIndex();
        }
    }

    [TestFixture]
    public class StreamParallelPerformanceTests : ECSTestsFixture
    {
        [SetUp]
        public override void Setup()
        {
            base.Setup();
            // Ensure lifecycle system is created
            World.GetOrCreateSystem<EventLifecycleUpdateSystem<StreamParallelTestEvent>>();
        }

        [Test, Performance]
        public void StreamParallelWrite_Stress_100K_Events()
        {
            var configEntity = m_Manager.CreateEntity(typeof(StreamParallelWriteConfig));

            int totalEvents = 100_000;
            int batchCount = 2048;
            int itemsPerBatch = totalEvents / batchCount + 1;

            m_Manager.SetComponentData(configEntity, new StreamParallelWriteConfig
            {
                ItemCount = batchCount,
                ItemsPerBatch = itemsPerBatch
            });

            var sys = World.CreateSystem<StreamParallelStressWriteSystem>();

            Measure.Method(() =>
            {
                sys.Update(World.Unmanaged);
                m_Manager.CompleteAllTrackedJobs();
            })
            .WarmupCount(3)
            .MeasurementCount(10)
            .Run();
        }

        [Test, Performance]
        public void StreamParallelWrite_Stress_HighFrequency_1000Frames()
        {
            var configEntity = m_Manager.CreateEntity(typeof(StreamParallelWriteConfig));
            int dailyCount = 1000; // 1000 events per frame

            m_Manager.SetComponentData(configEntity, new StreamParallelWriteConfig
            {
                ItemCount = dailyCount,
                ItemsPerBatch = 1 // Single item per index
            });

            var sys = World.CreateSystem<StreamParallelStressWriteSystem>();
            var lifecycleSys = World.GetOrCreateSystem<EventLifecycleUpdateSystem<StreamParallelTestEvent>>();

            Measure.Method(() =>
            {
                sys.Update(World.Unmanaged);
                m_Manager.CompleteAllTrackedJobs();

                // Simulate frame end lifecycle
                lifecycleSys.Update(World.Unmanaged);
            })
            .WarmupCount(1)
            .MeasurementCount(10)
            .Run();
        }

        [Test, Performance]
        public void StreamParallelWrite_Stress_CapacityGrowth()
        {
            var configEntity = m_Manager.CreateEntity(typeof(StreamParallelWriteConfig));
            int totalEvents = 100_000;

            // Force reset to small capacity on each run via system logic using InitialCapacity config
            m_Manager.SetComponentData(configEntity, new StreamParallelWriteConfig
            {
                ItemCount = totalEvents,
                ItemsPerBatch = 1,
                InitialCapacity = 128
            });

            var sys = World.CreateSystem<StreamParallelStressWriteSystem>();

            Measure.Method(() =>
            {
                sys.Update(World.Unmanaged);
                m_Manager.CompleteAllTrackedJobs();
            })
            .MeasurementCount(10)
            .Run();
        }
    }
}

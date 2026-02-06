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
    [DisableAutoCreation]
    partial struct StreamParallelStressWriteSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var config = SystemAPI.GetSingleton<ParallelWriteConfig>();
            var buffer = SystemAPI.GetSingletonRW<EventBuffer<ParallelTestEvent>>();

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
        public StreamParallelEventWriter<ParallelTestEvent> Writer;
        public int ItemsPerBatch;
        public int TotalLimit;

        public void Execute(int index)
        {
            Writer.BeginForEachIndex(index);
            int baseVal = index * ItemsPerBatch;
            for (int i = 0; i < ItemsPerBatch; i++)
            {
                // Simple write loop
                Writer.Write(new ParallelTestEvent { Value = baseVal + i });
            }
            Writer.EndForEachIndex();
        }
    }

    [BurstCompile]
    struct StressTestSingleItemJob : IJobParallelFor
    {
        public StreamParallelEventWriter<ParallelTestEvent> Writer;
        public void Execute(int index)
        {
            Writer.BeginForEachIndex(index);
            Writer.Write(new ParallelTestEvent { Value = index });
            Writer.EndForEachIndex();
        }
    }

    [TestFixture]
    public class StreamParallelPerformanceTests : ParallelPerformanceTestBase
    {
        [Test, Performance]
        public void StreamParallelWrite_Stress_100K_Events()
        {
            int totalEvents = 100_000;
            int batchCount = 2048;
            int itemsPerBatch = totalEvents / batchCount + 1;

            RunStressTest<StreamParallelStressWriteSystem>(batchCount, itemsPerBatch);
        }

        [Test, Performance]
        public void StreamParallelWrite_Stress_HighFrequency_1000Frames()
        {
            int dailyCount = 1000; // 1000 events per frame
            // Single item per index, 1000 indices
            RunStressTestWithLifecycle<StreamParallelStressWriteSystem>(dailyCount, 1);
        }

        [Test, Performance]
        public void StreamParallelWrite_Stress_CapacityGrowth()
        {
            int totalEvents = 100_000;
            RunStressTest<StreamParallelStressWriteSystem>(totalEvents, 1, 128);
        }
    }
}

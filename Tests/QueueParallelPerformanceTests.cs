using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.Tests;
using Unity.Jobs;
using IceEvents;
using Unity.PerformanceTesting;

namespace IceEvents.Tests
{
    [DisableAutoCreation]
    partial struct QueueParallelStressWriteSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var config = SystemAPI.GetSingleton<ParallelWriteConfig>();
            var buffer = SystemAPI.GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            var writerHandle = buffer.ValueRW.GetQueueParallelWriter(Allocator.TempJob);

            int jobCount = config.ItemCount;

            if (config.ItemsPerBatch > 1)
            {
                var job = new QueueStressTestJob
                {
                    Writer = writerHandle.Writer,
                    ItemsPerBatch = config.ItemsPerBatch,
                    BaseOffset = 0
                };
                state.Dependency = job.Schedule(jobCount, 32, state.Dependency);
            }
            else
            {
                var job = new QueueStressTestSingleItemJob
                {
                    Writer = writerHandle.Writer
                };
                state.Dependency = job.Schedule(jobCount, 64, state.Dependency);
            }

            writerHandle.ScheduleCommit(ref state);
        }
    }

    [BurstCompile]
    struct QueueStressTestJob : IJobParallelFor
    {
        public QueueParallelEventWriter<ParallelTestEvent> Writer;
        public int ItemsPerBatch;
        public int BaseOffset;

        public void Execute(int index)
        {
            // No BeginForEachIndex needed for Queue
            int baseVal = BaseOffset + (index * ItemsPerBatch);
            for (int i = 0; i < ItemsPerBatch; i++)
            {
                Writer.Write(new ParallelTestEvent { Value = baseVal + i, ThreadIndex = index });
            }
        }
    }

    [BurstCompile]
    struct QueueStressTestSingleItemJob : IJobParallelFor
    {
        public QueueParallelEventWriter<ParallelTestEvent> Writer;

        public void Execute(int index)
        {
            Writer.Write(new ParallelTestEvent { Value = index, ThreadIndex = index });
        }
    }

    [TestFixture]
    public class QueueParallelPerformanceTests : ParallelPerformanceTestBase
    {
        [Test, Performance]
        public void QueueParallelWrite_Stress_100K_Events()
        {
            int totalEvents = 100_000;
            int batchCount = 2048; // Number of parallel jobs
            int itemsPerBatch = totalEvents / batchCount + 1;

            RunStressTest<QueueParallelStressWriteSystem>(batchCount, itemsPerBatch);
        }

        [Test, Performance]
        public void QueueParallelWrite_Stress_HighFrequency_1000Frames()
        {
            int dailyCount = 1000; // 1000 events per frame
            // 1000 jobs, 1 item per job
            RunStressTestWithLifecycle<QueueParallelStressWriteSystem>(dailyCount, 1);
        }

        [Test, Performance]
        public void QueueParallelWrite_Stress_CapacityGrowth()
        {
            int totalEvents = 100_000;
            // Force low initial capacity to trigger NativeQueue blocks allocation
            RunStressTest<QueueParallelStressWriteSystem>(totalEvents, 1, 128);
        }
    }
}

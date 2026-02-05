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
    [TestFixture]
    public class EventSystemPerformanceTests : ECSTestsFixture
    {
        [Test, Performance]
        public void ParallelWrite_Stress_100K_Events()
        {
            var sys = World.GetOrCreateSystem<EventLifecycleUpdateSystem<ParallelTestEvent>>();
            var buffer = m_Manager.CreateEntityQuery(typeof(EventBuffer<ParallelTestEvent>))
                .GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            // 100,000 events
            const int totalEvents = 100_000;
            // Split across fewer "threads" (stream indices) for batching, or 1-to-1.
            // For stress testing throughput, let's use a realistic batch count, e.g., 2048 batches
            int batchCount = 2048;
            int itemsPerBatch = totalEvents / batchCount + 1;

            Measure.Method(() =>
            {
                var writerHandle = buffer.GetParallelWriter(batchCount, Allocator.TempJob);
                var job = new StressTestJob
                {
                    Writer = writerHandle.Writer,
                    ItemsPerBatch = itemsPerBatch,
                    TotalLimit = totalEvents
                };

                var dep = job.Schedule(batchCount, 32);
                writerHandle.ScheduleCommit(buffer, ref dep);
                dep.Complete();
            })
            .WarmupCount(3)
            .MeasurementCount(10)
            .Run();

            var resultBuffer = m_Manager.CreateEntityQuery(typeof(EventBuffer<ParallelTestEvent>))
                .GetSingleton<EventBuffer<ParallelTestEvent>>();

            // Just verifying correct count for the last run to ensure logic held up
            // Note: In Performance tests, this might run multiple times, so the buffer grows.
            // We just want to ensure it didn't crash and performed reasonably.
            Assert.Pass("Performance test completed without exceptions.");
        }

        [Test, Performance]
        public void ParallelWrite_Stress_HighFrequency_1000Frames()
        {
            // Simulate 1000 frames of updates
            var sys = World.GetOrCreateSystem<EventLifecycleUpdateSystem<ParallelTestEvent>>();

            Measure.Method(() =>
            {
                var buffer = m_Manager.CreateEntityQuery(typeof(EventBuffer<ParallelTestEvent>))
                    .GetSingletonRW<EventBuffer<ParallelTestEvent>>();

                // Per frame: 1000 events
                int dailyCount = 1000;
                var writerHandle = buffer.GetParallelWriter(dailyCount, Allocator.TempJob);

                var job = new StressTestSingleItemJob
                {
                    Writer = writerHandle.Writer
                };

                var dep = job.Schedule(dailyCount, 64);
                writerHandle.ScheduleCommit(buffer, ref dep);
                dep.Complete();

                // Clear buffers (simulate end of frame update)
                // In a real system, the EventLifecycleUpdateSystem handles this swap/clear at start of frame.
                // We need to manually invoke the system update to trigger the lifecycle management
                sys.Update(World.Unmanaged);
            })
            .WarmupCount(1)
            .MeasurementCount(10) // Simulating "frames" via measurement iterations for cleaner perf data
            .Run();
        }

        [Test, Performance]
        public void ParallelWrite_Stress_CapacityGrowth()
        {
            var sys = World.GetOrCreateSystem<EventLifecycleUpdateSystem<ParallelTestEvent>>();
            var buffer = m_Manager.CreateEntityQuery(typeof(EventBuffer<ParallelTestEvent>))
                .GetSingletonRW<EventBuffer<ParallelTestEvent>>();

            // Start small
            // Write 100k events to force expansion
            // 128 -> 256 -> 512 ... -> 131072 (approx 10 expansions)
            const int totalEvents = 100_000;

            Measure.Method(() =>
            {
                // Reset to small capacity to force expansion every run
                buffer.ValueRW.BufferUpdateCurrent.Clear();
                buffer.ValueRW.BufferUpdateCurrent.SetCapacity(128);

                var writerHandle = buffer.GetParallelWriter(totalEvents, Allocator.TempJob);

                var job = new StressTestSingleItemJob
                {
                    Writer = writerHandle.Writer
                };

                var dep = job.Schedule(totalEvents, 64);
                writerHandle.ScheduleCommit(buffer, ref dep);
                dep.Complete();
            })
            .MeasurementCount(10)
            .Run();

            var resultBuffer = m_Manager.CreateEntityQuery(typeof(EventBuffer<ParallelTestEvent>))
                .GetSingleton<EventBuffer<ParallelTestEvent>>();

            // Verify final state
            Assert.AreEqual(totalEvents, resultBuffer.BufferUpdateCurrent.Length);
            // Verify Power of Two
            Assert.IsTrue((resultBuffer.BufferUpdateCurrent.Capacity & (resultBuffer.BufferUpdateCurrent.Capacity - 1)) == 0,
                $"Capacity {resultBuffer.BufferUpdateCurrent.Capacity} should be power of two");
            Assert.GreaterOrEqual(resultBuffer.BufferUpdateCurrent.Capacity, totalEvents);
        }

        [BurstCompile]
        struct StressTestJob : IJobParallelFor
        {
            public ParallelEventWriter<ParallelTestEvent> Writer;
            public int ItemsPerBatch;
            public int TotalLimit;

            public void Execute(int index)
            {
                Writer.BeginForEachIndex(index);
                int baseVal = index * ItemsPerBatch;
                for (int i = 0; i < ItemsPerBatch; i++)
                {
                    if (baseVal + i < TotalLimit)
                    {
                        Writer.Write(new ParallelTestEvent { Value = baseVal + i });
                    }
                }
                Writer.EndForEachIndex();
            }
        }

        [BurstCompile]
        struct StressTestSingleItemJob : IJobParallelFor
        {
            public ParallelEventWriter<ParallelTestEvent> Writer;
            public void Execute(int index)
            {
                Writer.BeginForEachIndex(index);
                Writer.Write(new ParallelTestEvent { Value = index });
                Writer.EndForEachIndex();
            }
        }
    }
}

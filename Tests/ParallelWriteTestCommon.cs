using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.Tests;
using Unity.PerformanceTesting;
using IceEvents;

namespace IceEvents.Tests
{
    public struct ParallelTestEvent : IEvent
    {
        public int Value;
        public int ThreadIndex;
    }

    public struct ParallelWriteConfig : IComponentData
    {
        public int ItemCount;
        public int ItemOffset;
        public int ItemsPerBatch;
        public int InitialCapacity;
    }

    [TestFixture]
    public abstract class ParallelPerformanceTestBase : ECSTestsFixture
    {
        [SetUp]
        public override void Setup()
        {
            base.Setup();
            // Ensure lifecycle system is created common for all parallel tests
            World.GetOrCreateSystem<EventLifecycleUpdateSystem<ParallelTestEvent>>();
        }

        protected void RunStressTest<TSystem>(int itemCount, int itemsPerBatch, int initialCapacity = 0)
            where TSystem : unmanaged, ISystem
        {
            var configEntity = m_Manager.CreateEntity(typeof(ParallelWriteConfig));

            m_Manager.SetComponentData(configEntity, new ParallelWriteConfig
            {
                ItemCount = itemCount,
                ItemsPerBatch = itemsPerBatch,
                InitialCapacity = initialCapacity
            });

            var sys = World.CreateSystem<TSystem>();
            var lifecycleSys = World.GetOrCreateSystem<EventLifecycleUpdateSystem<ParallelTestEvent>>();

            Measure.Method(() =>
            {
                sys.Update(World.Unmanaged);
                m_Manager.CompleteAllTrackedJobs();
            })
            .WarmupCount(3)
            .MeasurementCount(10)
            .Run();
        }

        protected void RunStressTestWithLifecycle<TSystem>(int itemCount, int itemsPerBatch)
            where TSystem : unmanaged, ISystem
        {
            var configEntity = m_Manager.CreateEntity(typeof(ParallelWriteConfig));

            m_Manager.SetComponentData(configEntity, new ParallelWriteConfig
            {
                ItemCount = itemCount,
                ItemsPerBatch = itemsPerBatch
            });

            var sys = World.CreateSystem<TSystem>();
            var lifecycleSys = World.GetOrCreateSystem<EventLifecycleUpdateSystem<ParallelTestEvent>>();

            Measure.Method(() =>
            {
                sys.Update(World.Unmanaged);
                m_Manager.CompleteAllTrackedJobs();

                lifecycleSys.Update(World.Unmanaged);
            })
            .WarmupCount(1)
            .MeasurementCount(10)
            .Run();
        }
    }
}

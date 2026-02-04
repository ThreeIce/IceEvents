using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.Tests;
using IceEvents;
using Unity.Mathematics;

[assembly: RegisterGenericComponentType(typeof(IceEvents.EventBuffer<IceEvents.Tests.TestEvent>))]

namespace IceEvents.Tests
{
    public struct TestEvent : IEvent
    {
        public int Value;
    }

    [TestFixture]
    public partial class EventSystemTests : ECSTestsFixture
    {
        private EventBuffer<T> GetBuffer<T>() where T : unmanaged, IEvent
        {
            var query = m_Manager.CreateEntityQuery(typeof(EventBuffer<T>));
            return m_Manager.GetComponentData<EventBuffer<T>>(query.GetSingletonEntity());
        }

        [Test]
        public void Test_DualWrite_BothBuffersCaptured()
        {
            // Ensure system exists to create the singleton
            World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<TestEvent>>();
            
            var buffer = GetBuffer<TestEvent>();

            var writer = buffer.GetWriter();
            writer.Write(new TestEvent { Value = 42 });

            Assert.AreEqual(1, buffer.BufferUpdateCurrent.Length);
            Assert.AreEqual(1, buffer.BufferFixedCurrent.Length);
            Assert.AreEqual(buffer.BufferUpdateCurrent[0].SequenceID, buffer.BufferFixedCurrent[0].SequenceID);
        }

        [Test]
        public void Test_IndependentLifecycle()
        {
            var updateLifecycle = World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<TestEvent>>();
            var fixedLifecycle = World.GetOrCreateSystemManaged<EventLifecycleFixedSystem<TestEvent>>();
            
            var buffer = GetBuffer<TestEvent>();

            // 1. Initial State: Empty
            // 2. Add Events manually
            buffer.BufferUpdateCurrent.Add(new InternalEvent<TestEvent> { SequenceID = 1 });
            buffer.BufferFixedCurrent.Add(new InternalEvent<TestEvent> { SequenceID = 1 });

            // 3. Run ONLY Update Lifecycle
            updateLifecycle.Update();
            
            // IMPORTANT: Re-fetch the buffer to see the swapped fields!
            buffer = GetBuffer<TestEvent>();

            Assert.AreEqual(0, buffer.BufferUpdateCurrent.Length, "Update Current should be cleared (swapped)");
            Assert.AreEqual(1, buffer.BufferUpdatePrevious.Length, "Update Previous should contain data (swapped)");
            Assert.AreEqual(1, buffer.BufferFixedCurrent.Length, "Fixed Current should be UNTOUCHED by Update lifecycle");

            // 4. Run Fixed Lifecycle
            fixedLifecycle.Update();
            
            // Re-fetch again
            buffer = GetBuffer<TestEvent>();

            Assert.AreEqual(1, buffer.BufferFixedPrevious.Length, "Fixed Previous should now contain data");
            Assert.AreEqual(0, buffer.BufferFixedCurrent.Length, "Fixed Current should be cleared");
        }

        [Test]
        public void Test_Reader_Selection_And_Persistence()
        {
            World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<TestEvent>>();
            var buffer = GetBuffer<TestEvent>();

            // Add events manually
            buffer.BufferUpdateCurrent.Add(new InternalEvent<TestEvent> { SequenceID = 1, Data = new TestEvent { Value = 101 } });
            buffer.BufferUpdateCurrent.Add(new InternalEvent<TestEvent> { SequenceID = 2, Data = new TestEvent { Value = 102 } });

            // Simulating a Consumer
            var reader = new EventReader<TestEvent>(0, Allocator.Temp);
            
            // Read first 2 events
            int count = 0;
            foreach (var e in reader.ReadUpdate(buffer))
            {
                count++;
            }
            Assert.AreEqual(2, count);
            Assert.AreEqual(2uL, reader.LastReadID);

            // Add one more event
            buffer.BufferUpdateCurrent.Add(new InternalEvent<TestEvent> { SequenceID = 3, Data = new TestEvent { Value = 103 } });
            
            // Read again - should only see the NEW event (SequenceID 3)
            count = 0;
            foreach (var e in reader.ReadUpdate(buffer))
            {
                count++;
                Assert.AreEqual(103, e.Value);
            }
            Assert.AreEqual(1, count);
            Assert.AreEqual(3uL, reader.LastReadID);

            reader.Dispose();
        }
    }
}

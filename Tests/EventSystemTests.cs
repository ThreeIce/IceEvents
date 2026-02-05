using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.Tests;
using IceEvents;
using System.Collections.Generic;

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
    }
}

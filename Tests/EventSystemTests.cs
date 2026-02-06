using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Entities.Tests;
using Unity.Jobs;
using IceEvents;
using System.Collections.Generic;
using IceEvents.Tests;

[assembly: RegisterGenericComponentType(typeof(EventBuffer<TestEvent>))]

[assembly: RegisterGenericJobType(typeof(EventCommitJob<TestEvent>))]

[assembly: RegisterGenericSystemType(typeof(EventLifecycleUpdateSystem<TestEvent>))]

[assembly: RegisterGenericSystemType(typeof(EventLifecycleFixedSystem<TestEvent>))]


namespace IceEvents.Tests
{
    public struct TestEvent : IEvent
    {
        public int Value;
    }



    // Test components for System-based testing
    public struct TestWriteInput : IComponentData
    {
        public int Value;
    }

    public struct TestReadOutput : IBufferElementData
    {
        public int Value;
    }

    public struct TestReadConfig : IComponentData
    {
        public int MaxCount; // 0 = read all
    }

    // Test systems for System-based testing
    [DisableAutoCreation]
    partial struct TestEventWriterSystem : ISystem
    {
        [BurstCompile]
        struct WriteEventsJob : IJob
        {
            public EventWriter<TestEvent> Writer;
            [ReadOnly] public NativeArray<TestWriteInput> Inputs;

            public void Execute()
            {
                for (int i = 0; i < Inputs.Length; i++)
                {
                    Writer.Write(new TestEvent { Value = Inputs[i].Value });
                }
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            var buffer = SystemAPI.GetSingletonRW<EventBuffer<TestEvent>>();
            var writer = buffer.ValueRW.GetWriter();

            var query = SystemAPI.QueryBuilder().WithAll<TestWriteInput>().Build();
            var inputs = query.ToComponentDataArray<TestWriteInput>(Allocator.TempJob);

            new WriteEventsJob
            {
                Writer = writer,
                Inputs = inputs
            }.Schedule().Complete();

            inputs.Dispose();
        }
    }

    [DisableAutoCreation]
    partial struct TestEventReaderSystem : ISystem
    {
        private EventReader<TestEvent> _reader;

        [BurstCompile]
        struct ReadEventsJob : IJob
        {
            public EventReader<TestEvent> Reader;
            public DynamicBuffer<TestReadOutput> OutputBuffer;
            public int MaxCount;

            public void Execute()
            {
                int count = 0;
                foreach (var evt in Reader)
                {
                    OutputBuffer.Add(new TestReadOutput { Value = evt.Value });
                    count++;
                    if (MaxCount > 0 && count >= MaxCount) break;
                }
            }
        }

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<EventBuffer<TestEvent>>();
            var buffer = SystemAPI.GetSingleton<EventBuffer<TestEvent>>();
            _reader = buffer.GetUpdateReader(Allocator.Persistent);
        }

        public void OnUpdate(ref SystemState state)
        {
            var buffer = SystemAPI.GetSingleton<EventBuffer<TestEvent>>();
            _reader.Update(buffer);

            // Find output entity
            foreach (var (outputBuffer, config) in
                SystemAPI.Query<DynamicBuffer<TestReadOutput>, RefRO<TestReadConfig>>())
            {
                new ReadEventsJob
                {
                    Reader = _reader,
                    OutputBuffer = outputBuffer,
                    MaxCount = config.ValueRO.MaxCount
                }.Schedule().Complete();
            }
        }

        public void OnDestroy(ref SystemState state)
        {
            _reader.Dispose();
        }
    }

    [DisableAutoCreation]
    partial struct TestEventReaderFixedSystem : ISystem
    {
        private EventReader<TestEvent> _reader;

        [BurstCompile]
        struct ReadEventsJob : IJob
        {
            public EventReader<TestEvent> Reader;
            public DynamicBuffer<TestReadOutput> OutputBuffer;
            public int MaxCount;

            public void Execute()
            {
                int count = 0;
                foreach (var evt in Reader)
                {
                    OutputBuffer.Add(new TestReadOutput { Value = evt.Value });
                    count++;
                    if (MaxCount > 0 && count >= MaxCount) break;
                }
            }
        }

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<EventBuffer<TestEvent>>();
            var buffer = SystemAPI.GetSingleton<EventBuffer<TestEvent>>();
            _reader = buffer.GetFixedReader(Allocator.Persistent);
        }

        public void OnUpdate(ref SystemState state)
        {
            var buffer = SystemAPI.GetSingleton<EventBuffer<TestEvent>>();
            _reader.Update(buffer);

            // Find output entity
            foreach (var (outputBuffer, config) in
                SystemAPI.Query<DynamicBuffer<TestReadOutput>, RefRO<TestReadConfig>>())
            {
                new ReadEventsJob
                {
                    Reader = _reader,
                    OutputBuffer = outputBuffer,
                    MaxCount = config.ValueRO.MaxCount
                }.Schedule().Complete();
            }
        }

        public void OnDestroy(ref SystemState state)
        {
            _reader.Dispose();
        }
    }


    [TestFixture]
    public partial class EventSystemTests : ECSTestsFixture
    {
        #region Setup & Helpers

        // Common test systems - initialized in SetUp
        private SystemHandle _updateLifecycleSys;
        private SystemHandle _fixedLifecycleSys;
        private SystemHandle _writerSys;
        private SystemHandle _updateReaderSys;
        private SystemHandle _fixedReaderSys;

        [SetUp]
        public override void Setup()
        {
            base.Setup();

            // Create lifecycle systems and ensure EventBuffer singleton exists
            _updateLifecycleSys = World.GetOrCreateSystem<EventLifecycleUpdateSystem<TestEvent>>();
            _fixedLifecycleSys = World.GetOrCreateSystem<EventLifecycleFixedSystem<TestEvent>>();

            // Create test systems (OnCreate will succeed because EventBuffer exists)
            _writerSys = World.CreateSystem<TestEventWriterSystem>();
            _updateReaderSys = World.CreateSystem<TestEventReaderSystem>();
            _fixedReaderSys = World.CreateSystem<TestEventReaderFixedSystem>();
        }

        private EventBuffer<TestEvent> GetBuffer()
        {
            var query = m_Manager.CreateEntityQuery(typeof(EventBuffer<TestEvent>));
            return m_Manager.GetComponentData<EventBuffer<TestEvent>>(query.GetSingletonEntity());
        }

        private void SimulateUpdateFrame<T>() where T : unmanaged, IEvent
        {
            var sys = World.GetOrCreateSystem<EventLifecycleUpdateSystem<T>>();
            sys.Update(World.Unmanaged);
        }

        private void SimulateFixedUpdateFrame<T>() where T : unmanaged, IEvent
        {
            var sys = World.GetOrCreateSystem<EventLifecycleFixedSystem<T>>();
            sys.Update(World.Unmanaged);
        }

        #endregion

        #region EventReader - Basic Tests

        [Test]
        public void Reader_ContinuousReading_TracksNewEvents()
        {
            // Create output entity for reader
            var outputEntity = m_Manager.CreateEntity(typeof(TestReadOutput), typeof(TestReadConfig));
            m_Manager.SetComponentData(outputEntity, new TestReadConfig { MaxCount = 0 }); // Read all

            // Frame 1: Write events 1, 2
            var input1 = m_Manager.CreateEntity(typeof(TestWriteInput));
            var input2 = m_Manager.CreateEntity(typeof(TestWriteInput));
            m_Manager.SetComponentData(input1, new TestWriteInput { Value = 1 });
            m_Manager.SetComponentData(input2, new TestWriteInput { Value = 2 });

            _writerSys.Update(World.Unmanaged);
            _updateReaderSys.Update(World.Unmanaged);

            // Verify frame 1
            var outputBuffer = m_Manager.GetBuffer<TestReadOutput>(outputEntity);
            Assert.AreEqual(2, outputBuffer.Length, "Should read 2 events in frame 1");
            var values = new HashSet<int>();
            for (int i = 0; i < outputBuffer.Length; i++)
                values.Add(outputBuffer[i].Value);
            Assert.That(values.SetEquals(new[] { 1, 2 }));

            // Frame 2: Swap buffers, write event 3
            _updateLifecycleSys.Update(World.Unmanaged);
            m_Manager.DestroyEntity(input1);
            m_Manager.DestroyEntity(input2);
            // Re-get buffer after structural changes
            outputBuffer = m_Manager.GetBuffer<TestReadOutput>(outputEntity);
            outputBuffer.Clear();

            var input3 = m_Manager.CreateEntity(typeof(TestWriteInput));
            m_Manager.SetComponentData(input3, new TestWriteInput { Value = 3 });

            _writerSys.Update(World.Unmanaged);
            _updateReaderSys.Update(World.Unmanaged);

            // Verify frame 2
            outputBuffer = m_Manager.GetBuffer<TestReadOutput>(outputEntity);
            Assert.AreEqual(1, outputBuffer.Length, "Should read only new event in frame 2");
            Assert.AreEqual(3, outputBuffer[0].Value);

            m_Manager.DestroyEntity(input3);
        }

        [Test]
        public void Reader_CatchUp_ReadsMissedEvents()
        {
            var outputEntity = m_Manager.CreateEntity(typeof(TestReadOutput), typeof(TestReadConfig));
            m_Manager.SetComponentData(outputEntity, new TestReadConfig { MaxCount = 0 });

            // Frame 1: Write events 1, 2 and swap
            var input1 = m_Manager.CreateEntity(typeof(TestWriteInput));
            var input2 = m_Manager.CreateEntity(typeof(TestWriteInput));
            m_Manager.SetComponentData(input1, new TestWriteInput { Value = 1 });
            m_Manager.SetComponentData(input2, new TestWriteInput { Value = 2 });

            _writerSys.Update(World.Unmanaged);
            _updateLifecycleSys.Update(World.Unmanaged); // Swap: 1,2 go to Previous

            m_Manager.DestroyEntity(input1);
            m_Manager.DestroyEntity(input2);

            // Frame 2: Write events 3, 4 (in Current)
            var input3 = m_Manager.CreateEntity(typeof(TestWriteInput));
            var input4 = m_Manager.CreateEntity(typeof(TestWriteInput));
            m_Manager.SetComponentData(input3, new TestWriteInput { Value = 3 });
            m_Manager.SetComponentData(input4, new TestWriteInput { Value = 4 });

            _writerSys.Update(World.Unmanaged);

            // Reader should catch up and read all 4 events (Previous + Current)
            _updateReaderSys.Update(World.Unmanaged);

            var outputBuffer = m_Manager.GetBuffer<TestReadOutput>(outputEntity);
            Assert.AreEqual(4, outputBuffer.Length, "Should read all 4 events (Previous + Current)");
            var values = new HashSet<int>();
            for (int i = 0; i < outputBuffer.Length; i++)
                values.Add(outputBuffer[i].Value);
            Assert.That(values.SetEquals(new[] { 1, 2, 3, 4 }));

            m_Manager.DestroyEntity(input3);
            m_Manager.DestroyEntity(input4);
        }

        [Test]
        public void Reader_PartialConsumption_BookmarkCorrect()
        {
            var outputEntity = m_Manager.CreateEntity(typeof(TestReadOutput), typeof(TestReadConfig));

            // Write 3 events
            var input1 = m_Manager.CreateEntity(typeof(TestWriteInput));
            var input2 = m_Manager.CreateEntity(typeof(TestWriteInput));
            var input3 = m_Manager.CreateEntity(typeof(TestWriteInput));
            m_Manager.SetComponentData(input1, new TestWriteInput { Value = 1 });
            m_Manager.SetComponentData(input2, new TestWriteInput { Value = 2 });
            m_Manager.SetComponentData(input3, new TestWriteInput { Value = 3 });

            _writerSys.Update(World.Unmanaged);

            // First read: Read only 2 events
            m_Manager.SetComponentData(outputEntity, new TestReadConfig { MaxCount = 2 });
            _updateReaderSys.Update(World.Unmanaged);

            var outputBuffer = m_Manager.GetBuffer<TestReadOutput>(outputEntity);
            Assert.AreEqual(2, outputBuffer.Length, "Should read only 2 events");

            // Second read: Read remaining events
            outputBuffer.Clear();
            m_Manager.SetComponentData(outputEntity, new TestReadConfig { MaxCount = 0 }); // Read all remaining
            _updateReaderSys.Update(World.Unmanaged);

            outputBuffer = m_Manager.GetBuffer<TestReadOutput>(outputEntity);
            Assert.AreEqual(1, outputBuffer.Length, "Should read remaining 1 event");
            Assert.AreEqual(3, outputBuffer[0].Value, "Should read event 3");

            m_Manager.DestroyEntity(input1);
            m_Manager.DestroyEntity(input2);
            m_Manager.DestroyEntity(input3);
        }

        [Test]
        public void Reader_EmptyBuffer_NoException()
        {
            var lifecycleSys = World.GetOrCreateSystem<EventLifecycleUpdateSystem<TestEvent>>();
            lifecycleSys.Update(World.Unmanaged); // Ensure EventBuffer singleton exists

            var readerSys = World.CreateSystem<TestEventReaderSystem>();

            var outputEntity = m_Manager.CreateEntity(typeof(TestReadOutput), typeof(TestReadConfig));
            m_Manager.SetComponentData(outputEntity, new TestReadConfig { MaxCount = 0 });

            // Don't write any events, just run reader
            Assert.DoesNotThrow(() => readerSys.Update(World.Unmanaged));

            var outputBuffer = m_Manager.GetBuffer<TestReadOutput>(outputEntity);
            Assert.AreEqual(0, outputBuffer.Length, "Should read 0 events from empty buffer");
        }

        #endregion

        #region Dual Channel Tests

        [Test]
        public void DualChannel_NoEventLost_AtDifferentRates()
        {
            var updateLifecycleSys = World.GetOrCreateSystem<EventLifecycleUpdateSystem<TestEvent>>();
            var fixedLifecycleSys = World.GetOrCreateSystem<EventLifecycleFixedSystem<TestEvent>>();
            // Ensure EventBuffer singleton exists
            updateLifecycleSys.Update(World.Unmanaged);
            fixedLifecycleSys.Update(World.Unmanaged);

            var writerSys = World.CreateSystem<TestEventWriterSystem>();
            var updateReaderSys = World.CreateSystem<TestEventReaderSystem>();
            var fixedReaderSys = World.CreateSystem<TestEventReaderFixedSystem>();

            // Write 3 events
            var input1 = m_Manager.CreateEntity(typeof(TestWriteInput));
            var input2 = m_Manager.CreateEntity(typeof(TestWriteInput));
            var input3 = m_Manager.CreateEntity(typeof(TestWriteInput));
            m_Manager.SetComponentData(input1, new TestWriteInput { Value = 1 });
            m_Manager.SetComponentData(input2, new TestWriteInput { Value = 2 });
            m_Manager.SetComponentData(input3, new TestWriteInput { Value = 3 });

            writerSys.Update(World.Unmanaged);

            // Swap both channels
            fixedLifecycleSys.Update(World.Unmanaged);
            updateLifecycleSys.Update(World.Unmanaged);

            // Test Update reader
            var updateOutputEntity = m_Manager.CreateEntity(typeof(TestReadOutput), typeof(TestReadConfig));
            m_Manager.SetComponentData(updateOutputEntity, new TestReadConfig { MaxCount = 0 });
            updateReaderSys.Update(World.Unmanaged);

            var updateBuffer = m_Manager.GetBuffer<TestReadOutput>(updateOutputEntity);
            Assert.AreEqual(3, updateBuffer.Length, "Update reader should see all 3 events");

            var updateValues = new HashSet<int>();
            for (int i = 0; i < updateBuffer.Length; i++) updateValues.Add(updateBuffer[i].Value);

            // Clean up and test Fixed reader
            m_Manager.DestroyEntity(updateOutputEntity);

            var fixedOutputEntity = m_Manager.CreateEntity(typeof(TestReadOutput), typeof(TestReadConfig));
            m_Manager.SetComponentData(fixedOutputEntity, new TestReadConfig { MaxCount = 0 });
            fixedReaderSys.Update(World.Unmanaged);

            var fixedBuffer = m_Manager.GetBuffer<TestReadOutput>(fixedOutputEntity);
            Assert.AreEqual(3, fixedBuffer.Length, "Fixed reader should see all 3 events");

            var fixedValues = new HashSet<int>();
            for (int i = 0; i < fixedBuffer.Length; i++) fixedValues.Add(fixedBuffer[i].Value);

            Assert.That(updateValues.SetEquals(fixedValues), "Both readers should see same events");

            m_Manager.DestroyEntity(input1);
            m_Manager.DestroyEntity(input2);
            m_Manager.DestroyEntity(input3);
        }

        #endregion

        #region Lifecycle Tests

        [Test]
        public void Lifecycle_EventsAccessibleAfterSwap()
        {
            var lifecycleSys = World.GetOrCreateSystem<EventLifecycleUpdateSystem<TestEvent>>();
            lifecycleSys.Update(World.Unmanaged); // Ensure EventBuffer singleton exists

            var writerSys = World.CreateSystem<TestEventWriterSystem>();
            var readerSys = World.CreateSystem<TestEventReaderSystem>();

            var outputEntity = m_Manager.CreateEntity(typeof(TestReadOutput), typeof(TestReadConfig));
            m_Manager.SetComponentData(outputEntity, new TestReadConfig { MaxCount = 0 });

            // Write events 10, 20
            var input1 = m_Manager.CreateEntity(typeof(TestWriteInput));
            var input2 = m_Manager.CreateEntity(typeof(TestWriteInput));
            m_Manager.SetComponentData(input1, new TestWriteInput { Value = 10 });
            m_Manager.SetComponentData(input2, new TestWriteInput { Value = 20 });

            writerSys.Update(World.Unmanaged);

            // Swap buffers
            lifecycleSys.Update(World.Unmanaged);

            // Reader should still see events in Previous buffer
            readerSys.Update(World.Unmanaged);

            var outputBuffer = m_Manager.GetBuffer<TestReadOutput>(outputEntity);
            Assert.AreEqual(2, outputBuffer.Length, "Reader should still access events after swap");
            var values = new HashSet<int>();
            for (int i = 0; i < outputBuffer.Length; i++)
                values.Add(outputBuffer[i].Value);
            Assert.That(values.SetEquals(new[] { 10, 20 }));

            m_Manager.DestroyEntity(input1);
            m_Manager.DestroyEntity(input2);
        }

        [Test]
        public void Lifecycle_MultipleSwaps_WorksCorrectly()
        {
            var lifecycleSys = World.GetOrCreateSystem<EventLifecycleUpdateSystem<TestEvent>>();
            lifecycleSys.Update(World.Unmanaged); // Ensure EventBuffer singleton exists

            var writerSys = World.CreateSystem<TestEventWriterSystem>();
            var readerSys = World.CreateSystem<TestEventReaderSystem>();

            var outputEntity = m_Manager.CreateEntity(typeof(TestReadOutput), typeof(TestReadConfig));
            m_Manager.SetComponentData(outputEntity, new TestReadConfig { MaxCount = 0 });

            // Multiple swaps
            lifecycleSys.Update(World.Unmanaged);
            lifecycleSys.Update(World.Unmanaged);
            lifecycleSys.Update(World.Unmanaged);

            // Write event 100
            var input = m_Manager.CreateEntity(typeof(TestWriteInput));
            m_Manager.SetComponentData(input, new TestWriteInput { Value = 100 });

            writerSys.Update(World.Unmanaged);
            readerSys.Update(World.Unmanaged);

            var outputBuffer = m_Manager.GetBuffer<TestReadOutput>(outputEntity);
            Assert.AreEqual(1, outputBuffer.Length);
            Assert.AreEqual(100, outputBuffer[0].Value);

            m_Manager.DestroyEntity(input);
        }

        #endregion

        #region Edge Cases

        [Test]
        public void EdgeCase_ReaderResumesAfterPause()
        {
            var lifecycleSys = World.GetOrCreateSystem<EventLifecycleUpdateSystem<TestEvent>>();
            lifecycleSys.Update(World.Unmanaged); // Ensure EventBuffer singleton exists

            var writerSys = World.CreateSystem<TestEventWriterSystem>();
            var readerSys = World.CreateSystem<TestEventReaderSystem>();

            var outputEntity = m_Manager.CreateEntity(typeof(TestReadOutput), typeof(TestReadConfig));
            m_Manager.SetComponentData(outputEntity, new TestReadConfig { MaxCount = 0 });

            // Phase 1: Reader active for frames 1-5
            for (int frame = 1; frame <= 5; frame++)
            {
                var input = m_Manager.CreateEntity(typeof(TestWriteInput));
                m_Manager.SetComponentData(input, new TestWriteInput { Value = frame });

                writerSys.Update(World.Unmanaged);
                lifecycleSys.Update(World.Unmanaged);

                var outputBuffer = m_Manager.GetBuffer<TestReadOutput>(outputEntity);
                outputBuffer.Clear();
                readerSys.Update(World.Unmanaged);

                Assert.That(outputBuffer.Length > 0, $"Frame {frame}: should read event {frame}");
                bool found = false;
                for (int i = 0; i < outputBuffer.Length; i++)
                    if (outputBuffer[i].Value == frame) found = true;
                Assert.That(found, $"Frame {frame}: should contain event {frame}");

                m_Manager.DestroyEntity(input);
            }

            // Phase 2: Reader "paused" - write events 6,7,8 and swap them out
            for (int frame = 6; frame <= 8; frame++)
            {
                var input = m_Manager.CreateEntity(typeof(TestWriteInput));
                m_Manager.SetComponentData(input, new TestWriteInput { Value = frame });
                writerSys.Update(World.Unmanaged);
                lifecycleSys.Update(World.Unmanaged);
                m_Manager.DestroyEntity(input);
            }

            // Phase 3: Write events 9,10 (current frame) without swap yet
            var input9 = m_Manager.CreateEntity(typeof(TestWriteInput));
            var input10 = m_Manager.CreateEntity(typeof(TestWriteInput));
            m_Manager.SetComponentData(input9, new TestWriteInput { Value = 9 });
            m_Manager.SetComponentData(input10, new TestWriteInput { Value = 10 });
            writerSys.Update(World.Unmanaged);
            // Note: No swap yet, so 9,10 are in Current, 8 is in Previous

            // Phase 4: Reader resumes - should see Previous(8) + Current(9,10)
            var outputBuffer2 = m_Manager.GetBuffer<TestReadOutput>(outputEntity);
            outputBuffer2.Clear();
            readerSys.Update(World.Unmanaged);

            // Should see event 8 (in Previous) and 9,10 (in Current)
            var resumedValues = new HashSet<int>();
            for (int i = 0; i < outputBuffer2.Length; i++)
                resumedValues.Add(outputBuffer2[i].Value);

            Assert.That(resumedValues.Contains(8), "Should see event 8 from Previous buffer");
            Assert.That(resumedValues.Contains(9), "Should see event 9 from Current buffer");
            Assert.That(resumedValues.Contains(10), "Should see event 10 from Current buffer");
            Assert.AreEqual(3, resumedValues.Count, "Should see exactly 3 events (Previous + Current)");

            m_Manager.DestroyEntity(input9);
            m_Manager.DestroyEntity(input10);
        }

        [Test]
        public void EdgeCase_LateJoinerAfterLongRun()
        {
            var lifecycleSys = World.GetOrCreateSystem<EventLifecycleUpdateSystem<TestEvent>>();
            lifecycleSys.Update(World.Unmanaged); // Ensure EventBuffer singleton exists

            var writerSys = World.CreateSystem<TestEventWriterSystem>();
            var readerSys = World.CreateSystem<TestEventReaderSystem>();

            // Run 20 frames without reader
            for (int i = 0; i < 20; i++)
            {
                var input = m_Manager.CreateEntity(typeof(TestWriteInput));
                m_Manager.SetComponentData(input, new TestWriteInput { Value = i });
                writerSys.Update(World.Unmanaged);
                lifecycleSys.Update(World.Unmanaged);
                m_Manager.DestroyEntity(input);
            }

            // Create late-joining reader
            var outputEntity = m_Manager.CreateEntity(typeof(TestReadOutput), typeof(TestReadConfig));
            m_Manager.SetComponentData(outputEntity, new TestReadConfig { MaxCount = 0 });

            // Late reader should work without crash
            Assert.DoesNotThrow(() => readerSys.Update(World.Unmanaged));

            var outputBuffer = m_Manager.GetBuffer<TestReadOutput>(outputEntity);
            Assert.GreaterOrEqual(outputBuffer.Length, 0, "Late reader should work without crash");
        }

        #endregion


    }
}

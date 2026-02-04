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

        private void SimulateUpdateFrame<T>(EntityManager em) where T : unmanaged, IEvent
        {
            var sys = World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<T>>();
            sys.Update();
        }

        // [Test 1] 基础写入验证
        // 验证 Update 和 FixedUpdate 两个通道是否同时收到数据
        [Test]
        public void Test_1_DualWrite_Basicing()
        {
            World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<TestEvent>>();
            var buffer = GetBuffer<TestEvent>();

            var writer = buffer.GetWriter();
            writer.Write(new TestEvent { Value = 100 });
            writer.Write(new TestEvent { Value = 200 });

            // 验证长度
            Assert.AreEqual(2, buffer.BufferUpdateCurrent.Length);
            Assert.AreEqual(2, buffer.BufferFixedCurrent.Length);
            // 验证数据
            Assert.AreEqual(100, buffer.BufferUpdateCurrent[0].Value);
            Assert.AreEqual(200, buffer.BufferFixedCurrent[1].Value);
        }

        // [Test 2]生命周期 Swap 与 BaseID 连续性
        // 关键测试：验证 Swap 后 BaseID 是否正确累加，保证 ID 唯一性
        [Test]
        public void Test_2_Lifecycle_Swap_BaseID_Continuity()
        {
            var sys = World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<TestEvent>>();
            var buffer = GetBuffer<TestEvent>();

            // Frame 1: Write 2 events
            buffer.BufferUpdateCurrent.Add(new TestEvent { Value = 1 });
            buffer.BufferUpdateCurrent.Add(new TestEvent { Value = 2 });
            // Before Swap: BaseIDPrev = 0.
            Assert.AreEqual(0, buffer.BaseIdUpdatePrev);

            // --- END OF FRAME 1 ---
            sys.Update(); // Swap happens here

            // Verify Frame 1 State (Post-Swap)
            buffer = GetBuffer<TestEvent>();
            // Old Curr became Prev.
            Assert.AreEqual(2, buffer.BufferUpdatePrevious.Length);
            Assert.AreEqual(0, buffer.BufferUpdateCurrent.Length); // New Curr is empty
            // BaseIDPrev is still 0 because the OLD OLD Prev was length 0?
            // Wait. Logic: "New BasePrev = Old BasePrev + Old Prev.Length".
            // Old BasePrev = 0. Old Prev Length = 0 (Initial).
            // So New BasePrev = 0 + 0 = 0.
            Assert.AreEqual(0, buffer.BaseIdUpdatePrev);

            // Frame 2: Write 3 events
            buffer.BufferUpdateCurrent.Add(new TestEvent { Value = 3 });
            buffer.BufferUpdateCurrent.Add(new TestEvent { Value = 4 });
            buffer.BufferUpdateCurrent.Add(new TestEvent { Value = 5 });

            // --- END OF FRAME 2 ---
            sys.Update(); // Swap happens again

            // Verify Frame 2 State (Post-Swap)
            buffer = GetBuffer<TestEvent>();
            // Old BasePrev (0) + Old Prev Length (2) = 2.
            Assert.AreEqual(2, buffer.BaseIdUpdatePrev);
            // Prev list should now hold the events 3, 4, 5
            Assert.AreEqual(3, buffer.BufferUpdatePrevious.Length);
            Assert.AreEqual(3, buffer.BufferUpdatePrevious[0].Value);
        }

        // [Test 3] Reader 正常顺序读取
        // 模拟一个每帧都运行的 Reader，验证它能不漏读、不重复读
        [Test]
        public void Test_3_Reader_Continuous_Reading()
        {
            World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<TestEvent>>();
            var buffer = GetBuffer<TestEvent>();
            var reader = new EventReader<TestEvent>(0, Allocator.Temp); // Start from ID 0 (Fresh)

            // Frame 1: 2 Events
            buffer.BufferUpdateCurrent.Add(new TestEvent { Value = 1 });
            buffer.BufferUpdateCurrent.Add(new TestEvent { Value = 2 });

            // Reader Runs
            int count = 0;
            foreach (var e in reader.ReadUpdate(buffer)) count++;
            Assert.AreEqual(2, count);
            Assert.AreEqual(2uL, reader.LastReadID); // Should be at ID 2

            // Simulate Swap
            SimulateUpdateFrame<TestEvent>(m_Manager);
            buffer = GetBuffer<TestEvent>();

            // Frame 2: 1 Event
            buffer.BufferUpdateCurrent.Add(new TestEvent { Value = 3 });

            // Reader Runs Again
            // It should skip the events in Previous (ID 1, 2) because it already read them.
            // It should only read ID 3 from Current.
            count = 0;
            int lastVal = 0;
            foreach (var e in reader.ReadUpdate(buffer))
            {
                count++;
                lastVal = e.Value;
            }
            Assert.AreEqual(1, count);
            Assert.AreEqual(3, lastVal);
            Assert.AreEqual(3uL, reader.LastReadID);

            reader.Dispose();
        }

        // [Test 4] Reader 迟到追赶 (Late Joiner)
        // 验证如果 Reader 几帧没运行，能否正确跨越 Previous 和 Current 补齐数据
        [Test]
        public void Test_4_Reader_CatchUp()
        {
            var sys = World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<TestEvent>>();
            var buffer = GetBuffer<TestEvent>();
            var reader = new EventReader<TestEvent>(0, Allocator.Temp);
            // Reader is created but DOES NOT RUN yet.

            // Frame 1: Events 1, 2
            buffer.BufferUpdateCurrent.Add(new TestEvent { Value = 1 });
            buffer.BufferUpdateCurrent.Add(new TestEvent { Value = 2 });
            sys.Update(); // Swap. Ids 1,2 moves to Prev.

            // Frame 2: Events 3, 4
            buffer = GetBuffer<TestEvent>(); // Refresh
            buffer.BufferUpdateCurrent.Add(new TestEvent { Value = 3 });
            buffer.BufferUpdateCurrent.Add(new TestEvent { Value = 4 });

            // Now Reader Runs for the first time!
            // It should see:
            // Prev: [1, 2] (BaseID=0)
            // Curr: [3, 4] (BaseID=2)
            // It starts at 0.

            int count = 0;
            var values = new NativeList<int>(Allocator.Temp);
            foreach (var e in reader.ReadUpdate(buffer))
            {
                count++;
                values.Add(e.Value);
            }

            Assert.AreEqual(4, count);
            Assert.AreEqual(1, values[0]);
            Assert.AreEqual(2, values[1]);
            Assert.AreEqual(3, values[2]);
            Assert.AreEqual(4, values[3]);

            reader.Dispose();
            values.Dispose();
        }

        // [Test 5] 半途而废 (Partial Consumption)
        // 验证如果 process 循环中间 break 了，bookmark 是否正确停在中间
        [Test]
        public void Test_5_Reader_Partial_Consumption()
        {
            World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<TestEvent>>();
            var buffer = GetBuffer<TestEvent>();

            buffer.BufferUpdateCurrent.Add(new TestEvent { Value = 1 });
            buffer.BufferUpdateCurrent.Add(new TestEvent { Value = 2 });
            buffer.BufferUpdateCurrent.Add(new TestEvent { Value = 3 });

            var reader = new EventReader<TestEvent>(0, Allocator.Temp);

            // Read only 2 items then break
            int count = 0;
            foreach (var e in reader.ReadUpdate(buffer))
            {
                count++;
                if (count == 2) break;
            }

            Assert.AreEqual(2, count);
            Assert.AreEqual(2uL, reader.LastReadID, "Bookmark should be at 2");

            // Read again - should start from 3
            count = 0;
            foreach (var e in reader.ReadUpdate(buffer))
            {
                count++;
                Assert.AreEqual(3, e.Value);
            }
            Assert.AreEqual(1, count);
            Assert.AreEqual(3uL, reader.LastReadID);

            reader.Dispose();
        }

        // [Test 6] 空帧鲁棒性 (Zero Length Frames)
        // 验证连续几个空帧不会导致 BaseID 错乱或者 IndexOutOfRange
        [Test]
        public void Test_6_Zero_Length_Robustness()
        {
            var sys = World.GetOrCreateSystemManaged<EventLifecycleUpdateSystem<TestEvent>>();
            var buffer = GetBuffer<TestEvent>();

            // Frame 1: Empty
            sys.Update();
            // Frame 2: Empty
            sys.Update();
            // Frame 3: Data
            buffer = GetBuffer<TestEvent>();
            buffer.BufferUpdateCurrent.Add(new TestEvent { Value = 999 });

            var reader = new EventReader<TestEvent>(0, Allocator.Temp);
            int count = 0;
            foreach (var e in reader.ReadUpdate(buffer)) count++;

            Assert.AreEqual(1, count);
            Assert.AreEqual(999, reader.LastReadID);
            // BaseID Logic Check:
            // F1: BasePrev 0 + Len 0 = 0.
            // F2: BasePrev 0 + Len 0 = 0.
            // F3: BasePrev is still 0. Current starts at 0 (implicit).
            // Wait, logic check:
            // "New BasePrev = Old BasePrev + Old Prev.Length"
            // If Old Prev was correct, it works.

            reader.Dispose();
        }
    }
}

using Unity.Collections;
using Unity.Entities;

namespace IceEvents
{
    public interface IEvent { }

    public struct EventBuffer<T> : IComponentData where T : unmanaged, IEvent
    {
        public NativeList<T> BufferUpdateCurrent;
        public NativeList<T> BufferUpdatePrevious;
        public ulong BaseIdUpdatePrev;

        public NativeList<T> BufferFixedCurrent;
        public NativeList<T> BufferFixedPrevious;
        public ulong BaseIdFixedPrev;

        public bool IsCreated => BufferUpdateCurrent.IsCreated;
    }

    public struct EventWriter<T> where T : unmanaged, IEvent
    {
        internal NativeList<T> BufferUpdate;
        internal NativeList<T> BufferFixed;

        public void Write(T eventData)
        {
            BufferUpdate.Add(eventData);
            BufferFixed.Add(eventData);
        }
    }

    public enum EventLoopType { Update, FixedUpdate }
}

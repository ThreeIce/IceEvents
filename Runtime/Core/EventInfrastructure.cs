using Unity.Collections;
using Unity.Entities;

namespace IceEvents
{
    /// <summary>
    /// Base interface for all events in the IceEvents system.
    /// <para>IMPORTANT: For every concrete event type, you MUST register the <see cref="EventBuffer{T}"/> at the assembly level:</para>
    /// [assembly: RegisterGenericComponentType(typeof(EventBuffer&lt;MyEvent&gt;))]
    /// </summary>
    public interface IEvent { }

    /// <summary>
    /// Singleton component that stores the dual-loop buffers for a specific event type.
    /// Supports independent Update and FixedUpdate schedules.
    /// </summary>
    public struct EventBuffer<T> : IComponentData where T : unmanaged, IEvent
    {
        // View for Update Loop
        public NativeList<T> BufferUpdateCurrent;
        public NativeList<T> BufferUpdatePrevious;
        public ulong BaseIdUpdatePrev;

        // View for FixedUpdate Loop
        public NativeList<T> BufferFixedCurrent;
        public NativeList<T> BufferFixedPrevious;
        public ulong BaseIdFixedPrev;

        public bool IsCreated => BufferUpdateCurrent.IsCreated;
    }

    /// <summary>
    /// A helper struct for writing events from parallel jobs.
    /// Writes to both Update and FixedUpdate buffers simultaneously.
    /// </summary>
    public struct EventWriter<T> where T : unmanaged, IEvent
    {
        public NativeList<T>.ParallelWriter WriterUpdate;
        public NativeList<T>.ParallelWriter WriterFixed;

        public void Write(T eventData)
        {
            // Write to both channels
            WriterUpdate.AddNoResize(eventData);
            WriterFixed.AddNoResize(eventData);
        }
    }
}

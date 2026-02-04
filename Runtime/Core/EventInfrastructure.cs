using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;

namespace IceEvents
{
    /// <summary>
    /// Base interface for all events.
    /// IMPORTANT: For every implemented event type, you MUST register the generic components 
    /// at the assembly level to enable ECS memory layout generation:
    /// <code>
    /// [assembly: RegisterGenericComponentType(typeof(EventBuffer&lt;MyEvent&gt;))]
    /// [assembly: RegisterGenericComponentType(typeof(InternalEvent&lt;MyEvent&gt;))]
    /// </code>
    /// </summary>
    public interface IEvent { }

    /// <summary>
    /// Internal wrapper to associate an event with a global sequence ID.
    /// </summary>
    public struct InternalEvent<T> where T : unmanaged, IEvent
    {
        public ulong SequenceID;
        public T Data;
    }

    /// <summary>
    /// Singleton component that stores the dual-loop buffers for a specific event type.
    /// Supports independent Update and FixedUpdate schedules.
    /// </summary>
    public struct EventBuffer<T> : IComponentData where T : unmanaged, IEvent
    {
        // View for Update Loop
        public NativeList<InternalEvent<T>> BufferUpdateCurrent;
        public NativeList<InternalEvent<T>> BufferUpdatePrevious;

        // View for FixedUpdate Loop
        public NativeList<InternalEvent<T>> BufferFixedCurrent;
        public NativeList<InternalEvent<T>> BufferFixedPrevious;

        // Shared Atomic Counter
        public NativeArray<ulong> GlobalCounter;

        public bool IsCreated => BufferUpdateCurrent.IsCreated;
    }

    /// <summary>
    /// A helper struct for writing events from parallel jobs.
    /// Writes to both Update and FixedUpdate buffers simultaneously.
    /// </summary>
    public struct EventWriter<T> where T : unmanaged, IEvent
    {
        public NativeList<InternalEvent<T>>.ParallelWriter WriterUpdate;
        public NativeList<InternalEvent<T>>.ParallelWriter WriterFixed;

        [NativeDisableParallelForRestriction]
        public NativeArray<ulong> GlobalCounter;

        public unsafe void Write(T eventData)
        {
            // Atomic increment for a globally unique ID
            ulong* ptr = (ulong*)GlobalCounter.GetUnsafePtr();
            ulong id = (ulong)System.Threading.Interlocked.Increment(ref *(long*)ptr);

            var ev = new InternalEvent<T>
            {
                SequenceID = id,
                Data = eventData
            };

            // Write to both channels
            WriterUpdate.AddNoResize(ev);
            WriterFixed.AddNoResize(ev);
        }
    }
}

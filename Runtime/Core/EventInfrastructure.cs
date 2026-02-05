using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;

namespace IceEvents
{
    /// <summary>
    /// Base interface for all IceEvents. Implement this on unmanaged structs.
    /// <para>
    /// <b>Registration Requirements:</b> You MUST register your event type at the assembly level:
    /// <code>
    /// [assembly: RegisterGenericComponentType(typeof(EventBuffer&lt;MyEvent&gt;))] // Required
    /// [assembly: RegisterGenericJobType(typeof(EventCommitJob&lt;MyEvent&gt;))] // Optional: Only if using ParallelEventWriter
    /// </code>
    /// </para>
    /// </summary>
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

    /// <summary>
    /// Parallel event writer wrapping NativeStream.Writer for concurrent event writing.
    /// Requires BeginForEachIndex/EndForEachIndex calls to mark writing scope per thread.
    /// </summary>
    public struct ParallelEventWriter<T> where T : unmanaged, IEvent
    {
        internal NativeStream.Writer Writer;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void BeginForEachIndex(int threadIndex)
        {
            Writer.BeginForEachIndex(threadIndex);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Write(T eventData)
        {
            Writer.Write(eventData);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void EndForEachIndex()
        {
            Writer.EndForEachIndex();
        }
    }

    /// <summary>
    /// Handle for parallel event writer that manages the underlying NativeStream lifecycle.
    /// </summary>
    public struct ParallelEventWriterHandle<T> : IDisposable where T : unmanaged, IEvent
    {
        public ParallelEventWriter<T> Writer;
        internal NativeStream Stream;

        /// <summary>
        /// Always use ScheduleCommit. Use this only when you know what you are doing.
        /// </summary>
        public void Dispose()
        {
            Stream.Dispose();
        }

        /// <summary>
        /// Schedules the commit job to merge parallel-written events into the target buffer.
        /// Automatically disposes the NativeStream after the commit job completes.
        /// <para>
        /// <b>IMPORTANT:</b> You must register the commit job for your event type at the assembly level for Burst support:
        /// <code>[assembly: RegisterGenericJobType(typeof(EventCommitJob&lt;MyEvent&gt;))]</code>
        /// </para>
        /// </summary>
        public void ScheduleCommit(
            RefRW<EventBuffer<T>> targetBuffer,
            ref JobHandle dependency)
        {
            var job = new EventCommitJob<T>
            {
                StreamReader = Stream.AsReader(),
                BufferUpdate = targetBuffer.ValueRW.BufferUpdateCurrent,
                BufferFixed = targetBuffer.ValueRW.BufferFixedCurrent
            };

            var commitHandle = job.Schedule(dependency);
            var disposeHandle = Stream.Dispose(commitHandle);
            dependency = disposeHandle;
        }
    }

    public enum EventLoopType { Update, FixedUpdate }
}

using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace IceEvents
{
    /// <summary>
    /// Parallel event writer wrapping NativeQueue.ParallelWriter for concurrent event writing.
    /// Simpler to use than StreamParallelEventWriter as it doesn't require batch indices.
    /// </summary>
    public struct ParallelEventWriter<T> where T : unmanaged, IEvent
    {
        internal NativeQueue<T>.ParallelWriter Writer;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Write(T eventData)
        {
            Writer.Enqueue(eventData);
        }
    }

    /// <summary>
    /// Handle for queue-based parallel event writer that manages the underlying NativeQueue lifecycle.
    /// </summary>
    public struct ParallelEventWriterHandle<T> : System.IDisposable where T : unmanaged, IEvent
    {
        public ParallelEventWriter<T> Writer;
        internal NativeQueue<T> Queue;
        internal NativeList<T> BufferUpdate;
        internal NativeList<T> BufferFixed;

        public void Dispose()
        {
            if (Queue.IsCreated) Queue.Dispose();
        }

        /// <summary>
        /// Schedules the commit job to merge parallel-written events into the target buffer.
        /// Automatically disposes the NativeQueue after the commit job completes.
        /// </summary>
        public JobHandle ScheduleCommit(JobHandle inputDeps)
        {
            if (!Queue.IsCreated)
                return inputDeps;

            // [SAFE] Allocate dummy stream so Safety System is happy.
            var dummyStream = new NativeStream(1, Allocator.TempJob);

            // Reuse existing EventCommitJob
            var job = new EventCommitJob<T>
            {
                StreamReader = dummyStream.AsReader(),
                Queue = Queue,
                BufferUpdate = BufferUpdate,
                BufferFixed = BufferFixed
            };

            var commitHandle = job.Schedule(inputDeps);
            var disposeHandle = Queue.Dispose(commitHandle);
            disposeHandle = dummyStream.Dispose(disposeHandle); // Auto-dispose
            return disposeHandle;
        }

        /// <summary>
        /// Schedules the commit job and automatically updates SystemState.Dependency.
        /// </summary>
        public void ScheduleCommit(ref SystemState state)
        {
            state.Dependency = ScheduleCommit(state.Dependency);
        }
    }

    /// <summary>
    /// Extension methods for EventBuffer to support ParallelWriter.
    /// </summary>
    public static class ParallelEventBufferExtensions
    {
        /// <summary>
        /// Creates a queue-based parallel event writer handle.
        /// Use this when you don't want to manage batch indices manually.
        /// </summary>
        public static ParallelEventWriterHandle<T> GetParallelWriter<T>(
            this ref EventBuffer<T> buffer,
            Allocator allocator) where T : unmanaged, IEvent
        {
            var queue = new NativeQueue<T>(allocator);

            return new ParallelEventWriterHandle<T>
            {
                Writer = new ParallelEventWriter<T>
                {
                    Writer = queue.AsParallelWriter()
                },
                Queue = queue,
                BufferUpdate = buffer.BufferUpdateCurrent,
                BufferFixed = buffer.BufferFixedCurrent
            };
        }
    }
}

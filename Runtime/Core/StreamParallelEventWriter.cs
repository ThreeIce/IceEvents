using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace IceEvents
{
    /// <summary>
    /// Parallel event writer wrapping NativeStream.Writer for concurrent event writing.
    /// Requires BeginForEachIndex/EndForEachIndex calls to mark writing scope per thread.
    /// </summary>
    public struct StreamParallelEventWriter<T> where T : unmanaged, IEvent
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
    public struct StreamParallelEventWriterHandle<T> : IDisposable where T : unmanaged, IEvent
    {
        public StreamParallelEventWriter<T> Writer;
        internal NativeStream Stream;
        internal NativeList<T> BufferUpdate;
        internal NativeList<T> BufferFixed;
        private bool m_Committed;

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
        /// </summary>
        public JobHandle ScheduleCommit(JobHandle inputDeps)
        {
            if (m_Committed || !Stream.IsCreated)
                return inputDeps;

            m_Committed = true;

            // [SAFE] Allocate dummy queue so Safety System is happy.
            // Since it's TempJob, we must dispose it.
            var dummyQueue = new NativeQueue<T>(Allocator.TempJob);

            var job = new EventCommitJob<T>
            {
                StreamReader = Stream.AsReader(),
                Queue = dummyQueue,
                BufferUpdate = BufferUpdate,
                BufferFixed = BufferFixed
            };

            var commitHandle = job.Schedule(inputDeps);
            var disposeHandle = dummyQueue.Dispose(commitHandle); // Auto-dispose
            disposeHandle = Stream.Dispose(disposeHandle);
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
    /// Extension methods for EventBuffer to support StreamParallelWriter.
    /// </summary>
    public static class StreamParallelEventBufferExtensions
    {
        /// <summary>
        /// Creates a parallel event writer handle with specified batch count.
        /// The batch count must match the number of batches that will write to the stream.
        /// For IJobFor, this should match the batch count used in Schedule().
        /// </summary>
        public static StreamParallelEventWriterHandle<T> GetStreamParallelWriter<T>(
            this ref EventBuffer<T> buffer,
            int batchCount,
            Allocator allocator) where T : unmanaged, IEvent
        {
            if (batchCount <= 0)
                throw new System.ArgumentException("Batch count must be greater than 0", nameof(batchCount));

            var stream = new NativeStream(batchCount, allocator);
            return new StreamParallelEventWriterHandle<T>
            {
                Writer = new StreamParallelEventWriter<T>
                {
                    Writer = stream.AsWriter()
                },
                Stream = stream,
                BufferUpdate = buffer.BufferUpdateCurrent,
                BufferFixed = buffer.BufferFixedCurrent
            };
        }
    }
}

using Unity.Entities;
using Unity.Collections;

namespace IceEvents
{
    public static class EventExtensions
    {
        public static EventWriter<T> GetWriter<T>(this RefRW<EventBuffer<T>> buffer) where T : unmanaged, IEvent
        {
            return buffer.ValueRW.GetWriter();
        }

        public static EventReader<T> GetUpdateReader<T>(this RefRW<EventBuffer<T>> buffer, ulong startId, Allocator allocator)
            where T : unmanaged, IEvent
            => buffer.ValueRO.GetUpdateReader(startId, allocator);

        public static EventReader<T> GetFixedReader<T>(this RefRW<EventBuffer<T>> buffer, ulong startId, Allocator allocator)
            where T : unmanaged, IEvent
            => buffer.ValueRO.GetFixedReader(startId, allocator);

        public static EventReader<T> GetUpdateReader<T>(this RefRW<EventBuffer<T>> buffer, Allocator allocator)
            where T : unmanaged, IEvent
            => buffer.ValueRO.GetUpdateReader(allocator);

        public static EventReader<T> GetFixedReader<T>(this RefRW<EventBuffer<T>> buffer, Allocator allocator)
            where T : unmanaged, IEvent
            => buffer.ValueRO.GetFixedReader(allocator);

        /// <summary>
        /// Creates a parallel event writer handle with specified batch count.
        /// The batch count must match the number of batches that will write to the stream.
        /// For IJobFor, this should match the batch count used in Schedule().
        /// </summary>
        public static StreamParallelEventWriterHandle<T> GetStreamParallelWriter<T>(
            this RefRW<EventBuffer<T>> buffer,
            int batchCount,
            Allocator allocator) where T : unmanaged, IEvent
        {
            return buffer.ValueRW.GetStreamParallelWriter(batchCount, allocator);
        }
    }
}

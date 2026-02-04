using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;

namespace IceEvents
{
    public static class EventExtensions
    {
        /// <summary>
        /// Gets an EventWriter that writes to both Update and FixedUpdate loops.
        /// Requires obtaining the singleton via SystemAPI.GetSingletonRW to ensure dependency tracking.
        /// </summary>
        public static EventWriter<T> GetWriter<T>(this RefRW<EventBuffer<T>> buffer) where T : unmanaged, IEvent
        {
            return buffer.ValueRO.GetWriter();
        }

        /// <summary>
        /// Gets an EventWriter directly from the buffer. 
        /// Use this for testing or in contexts where RefRW is unavailable.
        /// Warning: Does not provide dependency tracking.
        /// </summary>
        public static EventWriter<T> GetWriter<T>(this in EventBuffer<T> buffer) where T : unmanaged, IEvent
        {
            return new EventWriter<T>
            {
                WriterUpdate = buffer.BufferUpdateCurrent.AsParallelWriter(),
                WriterFixed = buffer.BufferFixedCurrent.AsParallelWriter()
            };
        }

        /// <summary>
        /// Gets a reader initialized to 0 (reads oldest available events).
        /// </summary>
        public static EventReader<T> GetReader<T>(this in EventBuffer<T> buffer, Allocator allocator) where T : unmanaged, IEvent
        {
            return new EventReader<T>(0, allocator);
        }
    }

    /// <summary>
    /// A persistent cursor for reading events. 
    /// Wraps a NativeArray to allow seamless bookmark updates when used inside Jobs.
    /// Dispose() must be called when the reader is no longer needed.
    /// </summary>
    public struct EventReader<T> : System.IDisposable where T : unmanaged, IEvent
    {
        private NativeArray<ulong> _bookmark;

        /// <summary>
        /// The ID of the last event read by this reader.
        /// </summary>
        public ulong LastReadID => _bookmark.IsCreated ? _bookmark[0] : 0;

        public EventReader(ulong startId, Allocator allocator)
        {
            _bookmark = new NativeArray<ulong>(1, allocator);
            _bookmark[0] = startId;
        }

        public void Dispose()
        {
            if (_bookmark.IsCreated) _bookmark.Dispose();
        }

        /// <summary>
        /// Reads new events since the last call in the Update loop context.
        /// WARNING: A single EventReader instance is NOT thread-safe for concurrent reading (e.g., in a parallel Job).
        /// </summary>
        public Enumerator ReadUpdate(in EventBuffer<T> buffer)
        {
            return new Enumerator(buffer.BufferUpdatePrevious, buffer.BufferUpdateCurrent, _bookmark, buffer.BaseIdUpdatePrev);
        }

        /// <summary>
        /// Reads new events since the last call in the FixedUpdate loop context.
        /// WARNING: A single EventReader instance is NOT thread-safe for concurrent reading (e.g., in a parallel Job).
        /// </summary>
        public Enumerator ReadFixed(in EventBuffer<T> buffer)
        {
            return new Enumerator(buffer.BufferFixedPrevious, buffer.BufferFixedCurrent, _bookmark, buffer.BaseIdFixedPrev);
        }

        public struct Enumerator
        {
            private readonly NativeList<T> _prev;
            private readonly NativeList<T> _curr;
            private NativeArray<ulong> _bookmark;
            private readonly ulong _baseIdPrev;
            private readonly ulong _baseIdCurr;

            private int _indexPrev;
            private int _indexCurr;
            private T _currentElement;

            public Enumerator(NativeList<T> prev, NativeList<T> curr, NativeArray<ulong> bookmark, ulong baseIdPrev)
            {
                _prev = prev;
                _curr = curr;
                _bookmark = bookmark;
                _baseIdPrev = baseIdPrev;
                _baseIdCurr = baseIdPrev + (ulong)prev.Length;
                _currentElement = default;

                // Pre-calculate start indices
                // We want to read events with ID > bookmark.
                // IDs are 1-based: ID = Base + Index + 1.
                // We want Base + Index + 1 > Bookmark
                // Base + Index + 1 = Bookmark + 1 (Target ID)
                ulong targetId = bookmark[0] + 1;

                // Phase 1: Prev
                // Index = TargetID - BaseID - 1
                long relIndexPrev = (long)(targetId - _baseIdPrev) - 1;
                // Clamp: If we are behind (relIndex < 0), start at 0.
                if (relIndexPrev < 0) relIndexPrev = 0;
                // Subtract 1 because MoveNext pre-increments
                _indexPrev = (int)relIndexPrev - 1;

                // Phase 2: Curr
                long relIndexCurr = (long)(targetId - _baseIdCurr) - 1;
                if (relIndexCurr < 0) relIndexCurr = 0;
                _indexCurr = (int)relIndexCurr - 1;
            }

            public T Current => _currentElement;

            public bool MoveNext()
            {
                // Phase 1: Prev
                if (_indexPrev < _prev.Length - 1)
                {
                    _indexPrev++;
                    _currentElement = _prev[_indexPrev];
                    // Immediate bookmark update
                    // ID = Base + Index + 1
                    _bookmark[0] = _baseIdPrev + (ulong)_indexPrev + 1;
                    return true;
                }

                // Phase 2: Curr
                if (_indexCurr < _curr.Length - 1)
                {
                    _indexCurr++;
                    _currentElement = _curr[_indexCurr];
                    _bookmark[0] = _baseIdCurr + (ulong)_indexCurr + 1;
                    return true;
                }

                return false;
            }

            public Enumerator GetEnumerator() => this;
        }
    }
}

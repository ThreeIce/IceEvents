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
                WriterFixed = buffer.BufferFixedCurrent.AsParallelWriter(),
                GlobalCounter = buffer.GlobalCounter
            };
        }

        /// <summary>
        /// Gets a reader initialized to the current end of the buffer (skips existing events).
        /// </summary>
        public static EventReader<T> GetReader<T>(this in EventBuffer<T> buffer, Allocator allocator) where T : unmanaged, IEvent
        {
            return new EventReader<T>(buffer.GlobalCounter[0], allocator);
        }
    }

    /// <summary>
    /// A persistent cursor for reading events. 
    /// Wraps a NativeArray to allow seamless bookmark updates when used inside Jobs.
    /// Dispose() must be called when the reader is no longer needed.
    /// </summary>
    public struct EventReader<T> where T : unmanaged, IEvent
    {
        private NativeArray<ulong> _bookmark;

        /// <summary>
        /// The ID of the last event read by this reader.
        /// </summary>
        public ulong LastReadID => _bookmark.IsCreated ? _bookmark[0] : 0;

        public EventReader(Allocator allocator)
        {
            _bookmark = new NativeArray<ulong>(1, allocator);
        }

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
            return new Enumerator(buffer.BufferUpdatePrevious, buffer.BufferUpdateCurrent, _bookmark);
        }

        /// <summary>
        /// Reads new events since the last call in the FixedUpdate loop context.
        /// WARNING: A single EventReader instance is NOT thread-safe for concurrent reading (e.g., in a parallel Job).
        /// </summary>
        public Enumerator ReadFixed(in EventBuffer<T> buffer)
        {
            return new Enumerator(buffer.BufferFixedPrevious, buffer.BufferFixedCurrent, _bookmark);
        }

        public struct Enumerator
        {
            private readonly NativeList<InternalEvent<T>> _prev;
            private readonly NativeList<InternalEvent<T>> _curr;
            private NativeArray<ulong> _bookmark;

            private int _index;
            private bool _inCurrent;
            private readonly ulong _startId;

            public Enumerator(NativeList<InternalEvent<T>> prev, NativeList<InternalEvent<T>> curr, NativeArray<ulong> bookmark)
            {
                _prev = prev;
                _curr = curr;
                _bookmark = bookmark;
                _startId = bookmark[0];
                _index = -1;
                _inCurrent = false;
            }

            public T Current => _inCurrent ? _curr[_index].Data : _prev[_index].Data;

            public bool MoveNext()
            {
                if (!_inCurrent)
                {
                    _index++;
                    while (_index < _prev.Length)
                    {
                        var ev = _prev[_index];
                        if (ev.SequenceID > _startId)
                        {
                            _bookmark[0] = math.max(_bookmark[0], ev.SequenceID);
                            return true;
                        }
                        _index++;
                    }
                    _inCurrent = true;
                    _index = -1;
                }

                _index++;
                while (_index < _curr.Length)
                {
                    var ev = _curr[_index];
                    if (ev.SequenceID > _startId)
                    {
                        _bookmark[0] = math.max(_bookmark[0], ev.SequenceID);
                        return true;
                    }
                    _index++;
                }

                return false;
            }

            public Enumerator GetEnumerator() => this;
        }
    }
}

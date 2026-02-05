using Unity.Entities;
using Unity.Collections;

namespace IceEvents
{
    public static class EventExtensions
    {
        public static EventWriter<T> GetWriter<T>(this RefRW<EventBuffer<T>> buffer) where T : unmanaged, IEvent
        {
            return new EventWriter<T>
            {
                BufferUpdate = buffer.ValueRW.BufferUpdateCurrent,
                BufferFixed = buffer.ValueRW.BufferFixedCurrent
            };
        }

        public static EventWriter<T> GetWriter<T>(this in EventBuffer<T> buffer) where T : unmanaged, IEvent
        {
            return new EventWriter<T>
            {
                BufferUpdate = buffer.BufferUpdateCurrent,
                BufferFixed = buffer.BufferFixedCurrent
            };
        }

        public static EventReader<T> GetUpdateReader<T>(this in EventBuffer<T> buffer, ulong startId, Allocator allocator)
            where T : unmanaged, IEvent
            => new EventReader<T>(startId, EventLoopType.Update, allocator);

        public static EventReader<T> GetFixedReader<T>(this in EventBuffer<T> buffer, ulong startId, Allocator allocator)
            where T : unmanaged, IEvent
            => new EventReader<T>(startId, EventLoopType.FixedUpdate, allocator);

        public static EventReader<T> GetUpdateReader<T>(this in EventBuffer<T> buffer, Allocator allocator)
            where T : unmanaged, IEvent
            => new EventReader<T>(0, EventLoopType.Update, allocator);

        public static EventReader<T> GetFixedReader<T>(this in EventBuffer<T> buffer, Allocator allocator)
            where T : unmanaged, IEvent
            => new EventReader<T>(0, EventLoopType.FixedUpdate, allocator);
    }

    public struct EventReader<T> : System.IDisposable where T : unmanaged, IEvent
    {
        private NativeArray<ulong> _bookmark;
        private readonly EventLoopType _loopType;

        private NativeList<T> _prev;
        private NativeList<T> _curr;
        private ulong _baseIdPrev;

        public EventReader(ulong startId, EventLoopType loopType, Allocator allocator)
        {
            _bookmark = new NativeArray<ulong>(1, allocator);
            _bookmark[0] = startId;
            _loopType = loopType;
            _prev = default;
            _curr = default;
            _baseIdPrev = 0;
        }

        public void Update(in EventBuffer<T> buffer)
        {
            if (_loopType == EventLoopType.Update)
            {
                _prev = buffer.BufferUpdatePrevious;
                _curr = buffer.BufferUpdateCurrent;
                _baseIdPrev = buffer.BaseIdUpdatePrev;
            }
            else
            {
                _prev = buffer.BufferFixedPrevious;
                _curr = buffer.BufferFixedCurrent;
                _baseIdPrev = buffer.BaseIdFixedPrev;
            }
        }

        public Enumerator GetEnumerator()
        {
            return new Enumerator(_prev, _curr, _bookmark, _baseIdPrev);
        }

        public void Dispose()
        {
            if (_bookmark.IsCreated) _bookmark.Dispose();
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

                ulong targetId = bookmark[0] + 1;

                long relIndexPrev = (long)(targetId - _baseIdPrev) - 1;
                if (relIndexPrev < 0) relIndexPrev = 0;
                _indexPrev = (int)relIndexPrev - 1;

                long relIndexCurr = (long)(targetId - _baseIdCurr) - 1;
                if (relIndexCurr < 0) relIndexCurr = 0;
                _indexCurr = (int)relIndexCurr - 1;
            }

            public T Current => _currentElement;

            public bool MoveNext()
            {
                if (_indexPrev < _prev.Length - 1)
                {
                    _indexPrev++;
                    _currentElement = _prev[_indexPrev];
                    _bookmark[0] = _baseIdPrev + (ulong)_indexPrev + 1;
                    return true;
                }

                if (_indexCurr < _curr.Length - 1)
                {
                    _indexCurr++;
                    _currentElement = _curr[_indexCurr];
                    _bookmark[0] = _baseIdCurr + (ulong)_indexCurr + 1;
                    return true;
                }

                return false;
            }
        }
    }
}

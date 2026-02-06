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
    /// [assembly: RegisterGenericComponentType(typeof(EventBuffer&lt;MyEvent&gt;))]
    /// [assembly: RegisterGenericSystemType(typeof(EventLifecycleUpdateSystem&lt;MyEvent&gt;))]
    /// [assembly: RegisterGenericSystemType(typeof(EventLifecycleFixedSystem&lt;MyEvent&gt;))]
    /// [assembly: RegisterGenericJobType(typeof(EventCommitJob&lt;MyEvent&gt;))] // Optional: Only if using StreamParallelEventWriter
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

        public EventWriter<T> GetWriter()
        {
            return new EventWriter<T>
            {
                BufferUpdate = BufferUpdateCurrent,
                BufferFixed = BufferFixedCurrent
            };
        }

        public readonly EventReader<T> GetUpdateReader(ulong startId, Allocator allocator)
            => new(startId, EventLoopType.Update, allocator);

        public readonly EventReader<T> GetFixedReader(ulong startId, Allocator allocator)
            => new(startId, EventLoopType.FixedUpdate, allocator);

        public readonly EventReader<T> GetUpdateReader(Allocator allocator)
            => new(0, EventLoopType.Update, allocator);

        public readonly EventReader<T> GetFixedReader(Allocator allocator)
            => new(0, EventLoopType.FixedUpdate, allocator);

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

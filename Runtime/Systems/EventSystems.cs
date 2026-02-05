using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;

namespace IceEvents
{
    /// <summary>
    /// Job responsible for committing events from parallel writers (NativeStream) into the main EventBuffer.
    /// <para>
    /// <b>Requirement:</b> Use <code>[assembly: RegisterGenericJobType(typeof(EventCommitJob&lt;MyEvent&gt;))]</code>
    /// </para>
    /// </summary>
    [BurstCompile]
    public struct EventCommitJob<T> : IJob where T : unmanaged, IEvent
    {
        [ReadOnly]
        public NativeStream.Reader StreamReader;
        public NativeList<T> BufferUpdate;
        public NativeList<T> BufferFixed;

        public unsafe void Execute()
        {
            int totalCount = StreamReader.Count();

            EnsureCapacity(ref BufferUpdate, totalCount);
            EnsureCapacity(ref BufferFixed, totalCount);

            for (int i = 0; i < StreamReader.ForEachCount; i++)
            {
                StreamReader.BeginForEachIndex(i);
                int count = StreamReader.RemainingItemCount;

                for (int j = 0; j < count; j++)
                {
                    T item = StreamReader.Read<T>();
                    BufferUpdate.AddNoResize(item);
                    BufferFixed.AddNoResize(item);
                }

                StreamReader.EndForEachIndex();
            }
        }

        private static void EnsureCapacity(ref NativeList<T> list, int additionalCount)
        {
            int needed = list.Length + additionalCount;
            if (list.Capacity >= needed) return;
            list.Capacity = NextPowerOfTwo(needed);
        }

        private static int NextPowerOfTwo(int value)
        {
            if (value <= 1) return 1;
            value--;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            value++;
            return value;
        }
    }

    /// <summary>
    /// System responsible for the UPDATE loop buffer swap.
    /// </summary>
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct EventLifecycleUpdateSystem<T> : ISystem where T : unmanaged, IEvent
    {
        public readonly void OnCreate(ref SystemState state)
        {
            EnsureBufferInitialized<T>(state.EntityManager);
        }

        public readonly void OnUpdate(ref SystemState state)
        {
            var buffer = SystemAPI.GetSingletonRW<EventBuffer<T>>();

            // Update BaseID: New Base = Old Base + Length of discarded buffer
            buffer.ValueRW.BaseIdUpdatePrev += (ulong)buffer.ValueRW.BufferUpdatePrevious.Length;

            buffer.ValueRW.BufferUpdatePrevious.Clear();
            (buffer.ValueRW.BufferUpdateCurrent, buffer.ValueRW.BufferUpdatePrevious) = (buffer.ValueRW.BufferUpdatePrevious, buffer.ValueRW.BufferUpdateCurrent);
        }

        internal static void EnsureBufferInitialized<TEvent>(EntityManager em) where TEvent : unmanaged, IEvent
        {
            var query = em.CreateEntityQuery(ComponentType.ReadOnly<EventBuffer<TEvent>>());
            if (query.IsEmptyIgnoreFilter)
            {
                var entity = em.CreateEntity();
                em.AddComponentData(entity, new EventBuffer<TEvent>
                {
                    BufferUpdateCurrent = new NativeList<TEvent>(128, Allocator.Persistent),
                    BufferUpdatePrevious = new NativeList<TEvent>(128, Allocator.Persistent),
                    BufferFixedCurrent = new NativeList<TEvent>(128, Allocator.Persistent),
                    BufferFixedPrevious = new NativeList<TEvent>(128, Allocator.Persistent)
                });
            }
            else
            {
                throw new InvalidOperationException("EventBuffer<T> already exists");
            }
        }

        public readonly void OnDestroy(ref SystemState state)
        {
            var buffer = SystemAPI.GetSingletonRW<EventBuffer<T>>();
            // Check if already disposed (buffers are valid)
            if (buffer.ValueRW.BufferUpdateCurrent.IsCreated)
            {
                buffer.ValueRW.BufferUpdateCurrent.Dispose();
                buffer.ValueRW.BufferUpdatePrevious.Dispose();
                buffer.ValueRW.BufferFixedCurrent.Dispose();
                buffer.ValueRW.BufferFixedPrevious.Dispose();
            }
        }
    }

    /// <summary>
    /// System responsible for the FIXED loop buffer swap.
    /// </summary>
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup), OrderFirst = true)]
    public partial struct EventLifecycleFixedSystem<T> : ISystem where T : unmanaged, IEvent
    {
        public readonly void OnUpdate(ref SystemState state)
        {
            var buffer = SystemAPI.GetSingletonRW<EventBuffer<T>>();

            // Update BaseID
            buffer.ValueRW.BaseIdFixedPrev += (ulong)buffer.ValueRW.BufferFixedPrevious.Length;

            buffer.ValueRW.BufferFixedPrevious.Clear();
            (buffer.ValueRW.BufferFixedCurrent, buffer.ValueRW.BufferFixedPrevious) =
                (buffer.ValueRW.BufferFixedPrevious, buffer.ValueRW.BufferFixedCurrent);
        }
    }

    internal static class EventTypeFinder
    {
        public static List<Type> FindAllEventTypes()
        {
            var results = new List<Type>();
            var interfaceType = typeof(IEvent);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.FullName.StartsWith("System") || assembly.FullName.StartsWith("Unity")) continue;
                try
                {
                    foreach (var t in assembly.GetTypes())
                    {
                        if (interfaceType.IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract) results.Add(t);
                    }
                }
                catch { }
            }
            return results;
        }
    }
}

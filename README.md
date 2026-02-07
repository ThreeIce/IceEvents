# IceEvents

A high-performance, double-buffered event system for Unity DOTS (Data-Oriented Technology Stack). 

IceEvents provides a thread-safe, Burst-compatible way to send messages between ECS systems with automatic lifecycle management. It supports both single-threaded and parallel writing, making it ideal for everything from game logic triggers to high-frequency collision events.

## Features

- **Multi-Producer Multi-Consumer**: Fully supports multiple systems writing to and reading from the same event channel.
- **Zero Sync Points**: Fully asynchronous design. Writing events in jobs requires **zero** synchronization points on the main thread, ensuring maximum parallelism.
- **Parallel Writing**: Thread-safe, lock-free parallel writing from multiple jobs simultaneously.
- **Double-Buffered Reliability**: Events persist for up to two frames to ensure availability. *Note: Consumers must run every frame (or tick) to avoid missing data.*
- **Cross-Loop Consistency**: Safe to read `Update` events in `FixedUpdate` (and vice-versa). Events are never lost or read twice across loop boundaries.
- **Burst Compatible**: Fully optimized for Burst compilation.
- **Zero GC**: Allocation-free event writing during gameplay (amortized).

> [!WARNING]
> This project is still in early development and testing. Code reviews and testing are highly welcome!

## Installation

This package is designed for Unity DOTS projects. Ensure you have the Entities package installed.

## Getting Started

### 1. Define an Event
Create a struct implementing `IEvent`. It must be `unmanaged` to be Burst-compatible.

```csharp
using IceEvents;
using Unity.Entities;

public struct DamageEvent : IEvent
{
    public Entity Target;
    public float Amount;
}
```

### 2. Register the Event
**Crucial Step**: You must register your event type at the assembly level. This allows Unity's generic system creation to set up the necessary lifecycle systems automatically.

Add this to any `.cs` file in your assembly (e.g., `EventRegistration.cs`):

```csharp
using Unity.Entities;
using IceEvents;

// Required for the event storage
[assembly: RegisterGenericComponentType(typeof(EventBuffer<DamageEvent>))]

// Required for lifecycle management (clearing buffers, swapping double buffers)
[assembly: RegisterGenericSystemType(typeof(EventLifecycleUpdateSystem<DamageEvent>))]
[assembly: RegisterGenericSystemType(typeof(EventLifecycleFixedSystem<DamageEvent>))]

// Required ONLY if you use Parallel Writing (Parallel or StreamParallel)
[assembly: RegisterGenericJobType(typeof(EventCommitJob<DamageEvent>))]
```

## Writing Events

IceEvents supports three modes of writing.

### A. Single-Threaded (Simplest)
Use this within a standard `IJob` or on the Main Thread.

```csharp
[BurstCompile]
public partial struct GameEventSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        var buffer = SystemAPI.GetSingletonRW<EventBuffer<GameEvent>>();
        
        // Pass to a job
        new ProcessLogicJob
        {
            Writer = buffer.GetWriter()
        }.Schedule();
    }
}

[BurstCompile]
struct ProcessLogicJob : IJob
{
    public EventWriter<GameEvent> Writer;

    public void Execute()
    {
        Writer.Write(new GameEvent { Type = GameEventType.Ping });
    }
}
```

### B. Queue Parallel (Recommended for Jobs)
The `ParallelEventWriter` is the preferred way to write events from parallel jobs like `IJobEntity`. It is robust and easy to use.

```csharp
[BurstCompile]
public partial struct CollisionSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        var buffer = SystemAPI.GetSingletonRW<EventBuffer<CollisionEvent>>();
        
        // Create a parallel writer handle
        var writerHandle = buffer.ValueRW.GetParallelWriter(Allocator.TempJob);
        
        // Schedule your job
        new CollisionJob 
        { 
            Writer = writerHandle.Writer 
        }.ScheduleParallel();
        
        // CRITICAL: Schedule the commit job to merge events back to the main buffer
        writerHandle.ScheduleCommit(ref state);
    }
}

[BurstCompile]
partial struct CollisionJob : IJobEntity
{
    public ParallelEventWriter<CollisionEvent> Writer;
    
    void Execute(in Collision collision, in Entity entity)
    {
        if (collision.HasContact)
        {
            Writer.Write(new CollisionEvent { Entity = entity });
        }
    }
}
```

### C. Stream Parallel (Advanced)
Use `StreamParallelEventWriter` **only** if you need explicit control over batching index for extreme performance optimization. For 99% of use cases, Queue Parallel is sufficient and easier to use.

Key requirement: You must manage `BeginForEachIndex` and `EndForEachIndex` manually.

```csharp
// In your Job
public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, ...)
{
    Writer.BeginForEachIndex(unfilteredChunkIndex);
    // ... write events ...
    Writer.EndForEachIndex();
}
```

## Reading Events

Readers can traverse events from the previous frame. They remember their progress, so you can read a few events, pause, and resume later (though typically you consume all at once).

**Note on System Order**: Readers fail if the `EventBuffer` singleton doesn't exist yet. Ensure your reader system updates **after** the lifecycle system.

```csharp
[UpdateInGroup(typeof(SimulationSystemGroup))]
[CreateAfter(typeof(EventLifecycleUpdateSystem<DamageEvent>))] // Ensure buffer exists
public partial struct DamageMonitorSystem : ISystem
{
    private EventReader<DamageEvent> _reader;

    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<EventBuffer<DamageEvent>>();
        var buffer = SystemAPI.GetSingletonBuffer<EventBuffer<DamageEvent>>();
        _reader = buffer.GetUpdateReader(Allocator.Persistent);
    }

    public void OnUpdate(ref SystemState state)
    {
        var buffer = SystemAPI.GetSingleton<EventBuffer<DamageEvent>>();
        
        // 1. Update reader state on Main Thread (safe, just reading pointers)
        _reader.Update(buffer);
        
        // 2. Pass to Job (Zero Sync Point, fully async)
        new DamageReaderJob
        {
            Reader = _reader
        }.Schedule();
    }

    public void OnDestroy(ref SystemState state)
    {
        _reader.Dispose();
    }
}

[BurstCompile]
public struct DamageReaderJob : IJob
{
    public EventReader<DamageEvent> Reader;

    public void Execute()
    {
        // Safe to iterate in parallel with writers (reading previous frame)
        foreach (var evt in Reader)
        {
            // Logic here...
        }
    }
}
```

> [!CAUTION]
> **Main Thread Safety**: If you absolutely must read on the Main Thread (e.g., for `Debug.Log`), you **MUST** call `state.Dependency.Complete()` before iterating the reader. Failing to do so will cause race conditions with jobs writing to the buffer.


You can choose between `GetUpdateReader()` (for `Update` loop events) and `GetFixedReader()` (for `FixedUpdate` / Physics loop events). Use the one matching your system's update rate.

## Best Practices

1.  **Prefer `ParallelEventWriter`**: It handles load balancing automatically and prevents index out-of-bounds errors common with manual streams.
2.  **JobDeps**: When using parallel writers, always call `ScheduleCommit(ref state)`. This ensures that the temporary parallel buffers are merged into the main event list and that `state.Dependency` is correctly chained.
3.  **Main Thread Safety**: If you must write on the main thread, call `state.Dependency.Complete()` *before* accessing `SystemAPI.GetSingletonRW`.
4.  **Registration**: If you get "Unknown Type" errors, double-check your `[assembly: RegisterGeneric...]` attributes.

## Troubleshooting

-   **"The previously scheduled job writes to... BufferUpdate"**: You likely forgot to call `ScheduleCommit` in a parallel writing system, or you have a race condition where a reader is running at the same time as a writer.
-   **"IndexOutOfRangeException" in Stream Writer**: Your `batchCount` passed to `GetStreamParallelWriter` does not match the actual number of tasks/chunks, or you are using the wrong index in `BeginForEachIndex`. Use Queue Writer to avoid this.

## Roadmap / Todo

- [ ] **Roslyn-based API Optimization**: Simplify usage with source generators (基于 Roslyn 的 API 使用优化).
- [ ] **Unique ParallelWriter**: Support for strict uniqueness using `NativeHashSet` (基于 NativeHashSet 的无重 ParallelWriter).
- [ ] **Performance Optimization**: Further tuning for extreme high-frequency scenarios (更进一步的性能优化).

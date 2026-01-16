# Prim

A .NET continuation framework enabling suspend/resume of program execution with serializable state. Named after the primitive building blocks in Second Life.

## Overview

Prim allows you to:
- **Suspend** execution at any yield point and capture the entire call stack
- **Serialize** the captured state to JSON or MessagePack
- **Resume** execution from the saved state, even in a different process
- **Migrate** running computations across processes or machines

## Project Structure

```
Prim/
├── src/
│   ├── Prim.Core/           # Core types (HostFrameRecord, ContinuationState, etc.)
│   ├── Prim.Runtime/        # Execution context and runner
│   ├── Prim.Serialization/  # JSON and MessagePack serializers
│   ├── Prim.Roslyn/         # Source generator for [Continuable] methods
│   ├── Prim.Analysis/       # IL analysis (CFG, stack simulation)
│   └── Prim.Cecil/          # Bytecode rewriting with Mono.Cecil
├── tests/
│   ├── Prim.Tests.Unit/
│   ├── Prim.Tests.Integration/
│   ├── Prim.Tests.Roslyn/
│   └── Prim.Tests.Cecil/
└── samples/
    ├── Generator/           # Yield/resume demonstration
    └── MigrationDemo/       # Cross-process state migration
```

## Quick Start

### Basic Yield/Resume

```csharp
using Prim.Core;
using Prim.Runtime;

public class Counter
{
    public int Current { get; set; }

    public int CountTo(int target)
    {
        var context = ScriptContext.EnsureCurrent();
        const int methodToken = 12345;

        try
        {
            while (Current < target)
            {
                Current++;
                context.RequestYield();
                context.HandleYieldPoint(0, Current);
            }
            return Current;
        }
        catch (SuspendException ex)
        {
            var slots = FrameCapture.PackSlots(Current);
            var record = FrameCapture.CaptureFrame(methodToken, ex.YieldPointId, slots, ex.FrameChain);
            ex.FrameChain = record;
            throw;
        }
    }
}

// Usage
var counter = new Counter();
var result = ContinuationRunner.Run(() => counter.CountTo(5));

while (result is ContinuationResult<int>.Suspended suspended)
{
    Console.WriteLine($"Yielded at: {counter.Current}");
    result = ContinuationRunner.Resume<int>(suspended.State);
}

Console.WriteLine($"Completed: {((ContinuationResult<int>.Completed)result).Value}");
```

### Serialization and Migration

```csharp
using Prim.Serialization;

// Serialize state
var serializer = new JsonContinuationSerializer();
string json = serializer.SerializeToString(suspended.State);
File.WriteAllText("state.json", json);

// Later, in another process...
string json = File.ReadAllText("state.json");
var state = serializer.DeserializeFromString(json);
var result = ContinuationRunner.Resume<int>(state);
```

## Core Concepts

### HostFrameRecord
A linked list node representing a captured stack frame. Contains the method token, yield point ID, and captured local variables.

### ScriptContext
Thread-local context managing yield requests. Call `RequestYield()` to signal suspension, and `HandleYieldPoint()` at yield points to check and throw `SuspendException`.

### SuspendException
Special exception used for stack unwinding during suspension. Each catch block captures its frame state and re-throws, building the frame chain.

### ContinuationRunner
Entry point for running and resuming continuable computations. Handles the `SuspendException` and packages results as `Completed` or `Suspended`.

## Building

```bash
dotnet build Prim.sln
dotnet test
```

## Running Samples

```bash
dotnet run --project samples/Generator/Prim.Samples.Generator
dotnet run --project samples/MigrationDemo/Prim.Samples.MigrationDemo
```

## Target Framework

.NET Standard 2.0

## License

MIT

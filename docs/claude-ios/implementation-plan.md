# PlayerLoop Analytics Service — Implementation Plan & Considerations

## 1. Task Summary

Implement a new `IAnalyticsService` strategy called **PlayerLoopAnalyticsService** based on the external spec ("ResilientAnalytics — Implementation Spec"). The key requirement from the project owner: integrate it as a standard `IAnalyticsService` implementation selectable via the existing `AnalyticsConfig` ScriptableObject, matching the pattern used by the five existing strategies (Baseline, WorkerThread, Throttled, HybridDispatch, Awaitable).

---

## 2. Spec Analysis & Deviations

The external spec prescribes a standalone singleton architecture with its own `CircuitBreaker`, `AnalyticsQueue`, UniTask dependency, PlayerLoop injection, and `System.Threading.Channels`. After analysing the existing codebase, several deliberate deviations were made:

### 2.1 No UniTask

**Spec assumes:** `com.cysharp.unitask` is present.  
**Reality:** Not in `Packages/manifest.json`.  
**Decision:** Use standard `System.Threading.Tasks.Task` for background retry scheduling. `Task.Run` + `Task.Delay` provides the same async backoff without adding a dependency. If UniTask is added later, the `ScheduleRetry` method is the only place that needs updating.

### 2.2 No Standalone Singleton — IAnalyticsService Integration

**Spec assumes:** `ResilientAnalytics.Instance` singleton, `[RuntimeInitializeOnLoadMethod]` self-registration.  
**Reality:** The project has a well-established DI system (`DiContainer`) with `MonoInstaller` bindings, lifecycle interfaces (`IInitializable`, `ITickable`, `IDisposable`), and `[Inject]` attribute resolution.  
**Decision:** Implement as a plain C# class implementing `IAnalyticsService, IInitializable, ITickable, IDisposable`. Bound via `Container.Bind<PlayerLoopAnalyticsService>(addInterfaces: true)` in `MainGameInstaller`. This gives us:
- Automatic `[Inject]` field resolution (for `AnalyticsConfig`)
- `Initialize()` called by the container after all bindings resolve
- `Tick()` called every frame by the container's `Update()` loop
- `Dispose()` called on container teardown
- `IAnalyticsService` interface registered for `DiContainer.Resolve<IAnalyticsService>()`

### 2.3 ITickable Instead of PlayerLoop Injection

**Spec assumes:** Custom `PlayerLoopSystem` injected after Update, before LateUpdate.  
**Reality:** The DI container already calls `ITickable.Tick()` in its `Update()`, which is the same timing as the existing strategies.  
**Decision:** Use `ITickable` for consistency with all other strategies. The frame-budgeted drain in `Tick()` provides the same bounded-time semantics. PlayerLoop injection would add complexity (static delegates, manual lifecycle management) for marginal timing precision improvement. Noted as a future enhancement.

### 2.4 Reuse Existing CircuitBreaker & AnalyticsMetrics

**Spec assumes:** New sliding-window `CircuitBreaker` with `bool[]` circular buffer, `ReaderWriterLockSlim`, failure *rate* threshold.  
**Reality:** `IAnalyticsService` interface returns `CircuitBreaker CircuitBreaker { get; }` where `CircuitBreaker` is the concrete class in `Fortis.Analytics`. This class uses consecutive failure count (not rate) with `Interlocked` operations for thread safety.  
**Decision:** Reuse the existing `CircuitBreaker` and `AnalyticsMetrics` classes. They are already thread-safe, battle-tested across five strategies, and the interface requires these exact types. A sliding-window variant is noted as a future enhancement that would require either:
  - Refactoring `CircuitBreaker` to be abstract/interface-based
  - Or creating a new `ICircuitBreaker` interface and updating `IAnalyticsService`

### 2.5 No System.Threading.Channels

**Spec assumes:** `Channel<AnalyticsEvent>` for retry pipeline.  
**Decision:** `ConcurrentQueue` (already used by all existing strategies) handles the intake pipeline. Background retries simply re-enqueue to the same `ConcurrentQueue` after the delay. No channel needed — the frame drain naturally picks up retried events.

### 2.6 No Assembly Definitions

**Spec assumes:** `Analytics.Runtime.asmdef` and `Analytics.Editor.asmdef`.  
**Reality:** The existing codebase has no asmdef files — all code compiles into `Assembly-CSharp`. An asmdef for the new code would break references to `IAnalyticsService`, `CircuitBreaker`, `AnalyticsMetrics`, `AnalyticsConfig`, and `DiContainer` (all in `Assembly-CSharp`).  
**Decision:** Skip asmdef files. The Editor scripts are placed in an `Editor/` folder which Unity automatically excludes from builds.

---

## 3. Architecture Decisions

### 3.1 Threading Model

```
Any Thread ──SendEvent()──► ConcurrentQueue  (lock-free enqueue)
                                   │
Main Thread ──Tick()──────────────►│ drain within FrameBudgetMs
                                   │
                    UnstableLegacyService.SendEvent()  (main thread, safe)
                                   │
                          ┌────────┴────────┐
                          │                 │
                     success            failure
                          │                 │
                   RecordSuccess    RecordFailure + ScheduleRetry
                          │                 │
                          │          Task.Run(backoff → re-enqueue)
                          │                 │
                          └────────┬────────┘
                                   │
                            next frame Tick()
```

**Why main-thread dispatch:** `UnstableLegacyService.SendEvent()` calls `UnityEngine.Random.value` and `Debug.Log()`. Both require the main thread. Every strategy in the project that directly calls the SDK does so on the main thread.

**Why background retry:** The exponential backoff delay (500ms–8s) should not block the main thread. `Task.Run` + `Task.Delay` runs on the ThreadPool. After the delay, the event is re-enqueued to the `ConcurrentQueue` and the next `Tick()` picks it up.

### 3.2 Frame Budget

The key innovation over existing strategies:

| Strategy | Per-frame limit |
|---|---|
| Baseline | 5 events (hardcoded) |
| Throttled | 1 event per frame |
| HybridDispatch | 1 SDK call per frame |
| Awaitable | 1 event per frame |
| **PlayerLoop** | **Time-based: configurable ms budget** |

A `Stopwatch` measures wall-clock time. The drain loop exits when either:
- The budget is exhausted
- The queue is empty

This adapts to variable SDK latency (0ms normal, 500ms hitch) rather than using a fixed event count.

### 3.3 Pending Queue vs Retry Buffer

The spec distinguishes between:
1. **Pending queue** — events blocked by an open circuit, replayed when circuit closes
2. **Retry via backoff** — events that failed execution, retried after a delay

The implementation combines both concepts:
- When circuit is **open**: events go to `_pendingQueue` (main-thread `Queue<T>`, bounded by `MaxRetryBufferSize`). Flushed back to intake when circuit transitions to Closed.
- When SDK call **fails**: events are retried via `Task.Run` with exponential backoff if `RetryPolicy.ShouldRetry()` returns true. After the delay, they're re-enqueued to the intake `ConcurrentQueue`.

### 3.4 Callback Semantics

- First attempt: callback is invoked with `true` (success) or `false` (failure)
- Retries: callback is set to `null` to avoid double-invocation
- Pending queue: callback is invoked with `false` before buffering

This matches the existing strategies' behaviour where callbacks fire once on the initial attempt.

---

## 4. File Inventory

### Created

| File | Purpose |
|---|---|
| `Assets/Analytics/Runtime/IFirebaseReporter.cs` | Interface for exception reporting |
| `Assets/Analytics/Runtime/FirebaseCrashlyticsReporter.cs` | `#if FIREBASE_CRASHLYTICS` real impl + `NullFirebaseReporter` fallback |
| `Assets/Analytics/Runtime/RetryPolicy.cs` | `RetryConfig` struct + static `ShouldRetry`/`GetDelay` methods |
| `Assets/Analytics/Runtime/PlayerLoopAnalyticsService.cs` | Main `IAnalyticsService` implementation |
| `Assets/Analytics/Runtime/AnalyticsMonitor.cs` | Empty MonoBehaviour for custom inspector |
| `Assets/Analytics/Editor/ResilientAnalyticsEditorWindow.cs` | Editor window (Fortis → Analytics Monitor) |
| `Assets/Analytics/Editor/ResilientAnalyticsInspector.cs` | Custom inspector for AnalyticsMonitor |
| `Assets/Analytics/README.md` | Module documentation |

### Modified

| File | Change |
|---|---|
| `Assets/Code/AnalyticsConfig.cs` | Added `PlayerLoop` enum value, `FrameBudgetMs` field, `MaxRetryAttempts` field |
| `Assets/Code/MainGameInstaller.cs` | Added `using` and `case AnalyticsImplementation.PlayerLoop` binding |

### Not Modified

| File | Reason |
|---|---|
| `Assets/Code/UnstableService.cs` | Read-only mock — must not be modified |
| `Assets/Code/IAnalyticsService.cs` | Interface unchanged — new impl conforms to it |
| `Assets/Code/Claude-Implementation/CircuitBreaker.cs` | Reused as-is |
| `Assets/Code/Claude-Implementation/AnalyticsMetrics.cs` | Reused as-is |

---

## 5. Thread Safety Analysis

| Component | Thread Safety Mechanism |
|---|---|
| `_intakeQueue` (`ConcurrentQueue<T>`) | Lock-free, safe for concurrent enqueue from any thread + dequeue from main thread |
| `_pendingQueue` (`Queue<T>`) | Only accessed in `Tick()` (main thread) — no synchronisation needed |
| `CircuitBreaker` | Uses `Interlocked` operations throughout |
| `AnalyticsMetrics` | Uses `Interlocked.Increment` / `Interlocked.Add` / `Interlocked.Read` |
| `RetryPolicy` | Stateless static methods + `[ThreadStatic]` `System.Random` |
| `_flushRequested` (`volatile bool`) | Written from CircuitBreaker callback (possibly background), read from `Tick()` (main thread) |
| `_cts` (`CancellationTokenSource`) | Created once in `Initialize()`, cancelled once in `Dispose()` |

### Race condition considerations

1. **Retry re-enqueue + Tick dequeue:** Safe — `ConcurrentQueue.Enqueue()` from ThreadPool + `TryDequeue()` from main thread are designed for this pattern.
2. **Circuit state change during Tick:** `AllowRequest()` is atomic. If the circuit opens mid-drain, subsequent iterations will call `BufferForPending()`.
3. **Dispose during pending retries:** `_cts.Cancel()` cancels all pending `Task.Delay` calls. The `TaskCanceledException` is caught in the retry lambda.

---

## 6. Config Changes

Added to `AnalyticsConfig.cs`:

```csharp
// New enum value (position 5 — safe for serialization)
PlayerLoop

// New fields
[Header("Frame Budget (PlayerLoop)")]
public float FrameBudgetMs = 8f;

[Header("Retry Policy (PlayerLoop)")]
public int MaxRetryAttempts = 4;
```

These fields are ignored by other strategies. The `AnalyticsConfig.asset` doesn't need updating — new fields take their default values.

---

## 7. Editor Tools

### ResilientAnalyticsEditorWindow

- Menu: **Fortis → Analytics Monitor** (distinct from existing **Tools → Analytics Monitor**)
- Auto-repaints every 500ms via `EditorApplication.update`
- Color-coded circuit state (green/red/amber)
- Editable frame budget field (writes back to `AnalyticsConfig` at runtime)
- Test controls: single event, 20-event burst, force reset
- Guards all reads with null checks + friendly message outside Play Mode

### ResilientAnalyticsInspector

- Custom editor for `AnalyticsMonitor` MonoBehaviour
- Condensed read-only panel — state dot, failure rate, saved hitch, sent/dropped, pending, budget
- Same 500ms repaint interval

---

## 8. What Was NOT Implemented (and Why)

| Spec Item | Reason Skipped |
|---|---|
| `AnalyticsEvent.cs` (top-level class) | Used internal `QueuedEvent` struct — no external consumers need this type |
| `AnalyticsQueue.cs` (separate class) | Queue logic is integrated into `PlayerLoopAnalyticsService` to avoid indirection and keep the `IAnalyticsService` implementation self-contained |
| `CircuitState.cs` | Already exists in `Fortis.Analytics` namespace |
| Sliding-window CircuitBreaker | `IAnalyticsService` interface requires the existing `CircuitBreaker` type |
| `System.Threading.Channels` | `ConcurrentQueue` + `Task.Run` achieves the same pipeline without extra allocations |
| UniTask | Not in the project. `Task`-based async works identically |
| Assembly definitions | Would break references to Assembly-CSharp types |
| PlayerLoop injection | `ITickable` is the established pattern; PlayerLoop noted as future improvement |

---

## 9. Future Improvements

1. **Sliding-window circuit breaker** — Extract `ICircuitBreaker` interface, implement `SlidingWindowCircuitBreaker` with `bool[]` ring buffer and failure rate threshold
2. **PlayerLoop injection** — Custom `PlayerLoopSystem` for sub-frame timing precision, independent of DI container
3. **Persistent event log** — Serialize dropped events to `Application.persistentDataPath` for later replay
4. **Per-event priority** — High-priority events (purchases, auth) bypass frame budget; low-priority events (telemetry) are deferrable
5. **Remote config** — Fetch circuit breaker thresholds and frame budget from a remote config service
6. **Unit tests** — NSubstitute mocks for `UnstableLegacyService`, deterministic `CircuitBreaker` tests with injectable timestamps, `RetryPolicy` delay/jitter verification
7. **Event batching** — Coalesce multiple events into a single SDK call where the SDK supports it
8. **Metrics persistence** — Write session metrics to disk for post-mortem analysis

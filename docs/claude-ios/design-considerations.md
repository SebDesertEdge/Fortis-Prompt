# Design Considerations — PlayerLoop Analytics Service

## Why This Approach?

The external spec proposed a standalone resilience layer with its own lifecycle management (singleton, PlayerLoop injection, UniTask). The existing project, however, already has a mature pattern for analytics strategies: `IAnalyticsService` + DI container + `AnalyticsConfig` toggle. Integrating as a 6th strategy option was the right call because:

1. **Zero onboarding friction** — feature teams already know how to switch strategies in the config
2. **Shared tooling** — the existing `Tools → Analytics Monitor` editor window works with any `IAnalyticsService`, including this one
3. **Consistent lifecycle** — Initialize/Tick/Dispose managed by the DI container, same as every other service
4. **A/B comparison** — can switch between strategies at edit time to compare behaviour under the same conditions

---

## Key Trade-off: Frame Budget vs Event Count

The existing strategies limit per-frame work by **event count** (5 for Baseline, 1 for Throttled/HybridDispatch/Awaitable). This new strategy limits by **wall-clock time** (default 8ms).

**Advantages of time-based budget:**
- Adapts to variable SDK latency. A 500ms hitch consumes the entire budget on one event; a batch of fast successes processes many events in the same 8ms.
- Maps directly to frame time, making it easy to reason about performance impact.
- Tunable at runtime via the editor window without code changes.

**Disadvantage:**
- `Stopwatch` precision is ~1μs on modern hardware, but there's inherent overhead in checking it each iteration. For the analytics use case (events take 0–500ms each), this overhead is negligible.

---

## Why Not Background Thread for SDK Calls?

The `WorkerThread` (ResilientAnalytics) strategy calls `SendEvent()` on a background thread. This avoids main-thread hitches entirely but has a critical issue: `UnstableLegacyService.SendEvent()` calls `UnityEngine.Random.value`, which throws on background threads in development builds and has undefined behaviour in release builds.

The PlayerLoop strategy accepts that the SDK **must** run on the main thread and instead mitigates hitches through:
1. **Frame budget** — if a hitch occurs, it consumes the budget and no more events are processed that frame
2. **Circuit breaker** — after repeated failures, dispatch halts entirely until the SDK recovers
3. **Retry on background** — only the delay/scheduling runs off main thread; the actual re-attempt happens in a future frame's main-thread drain

This is the same approach as HybridDispatch, with the addition of time-based budgeting and exponential backoff.

---

## Why Reuse the Existing CircuitBreaker?

The spec's sliding-window circuit breaker (failure *rate* over last N attempts) is arguably more sophisticated than the existing consecutive-failure-count breaker. However:

- `IAnalyticsService.CircuitBreaker` returns the concrete `CircuitBreaker` class, not an interface
- Changing this would require modifying the interface and all 5 existing implementations
- The existing breaker is already thread-safe and well-tested
- The consecutive-failure model is simpler to reason about and debug

The sliding-window approach is documented as a future improvement. When implemented, it should involve extracting an `ICircuitBreaker` interface.

---

## Retry Strategy: Why Exponential Backoff?

Constant-interval retry (e.g., retry every 500ms) risks hammering a failing SDK and worsening the problem. Exponential backoff with jitter:

- **Spreads retries over time** — `500ms → 1s → 2s → 4s` (capped at 8s)
- **Jitter prevents thundering herd** — if 100 events fail simultaneously, their retries don't all land on the same frame
- **Bounded attempts** — max 4 retries then drop, preventing unbounded queue growth

The `RetryPolicy` is a pure static utility class with no Unity dependencies, making it trivially unit-testable.

---

## CancellationToken for Cleanup

When the service is disposed (play mode exit, scene unload), pending retry tasks on the ThreadPool must be cancelled. Without cancellation, `Task.Delay` continuations would fire after the service is torn down, potentially enqueueing to a null queue.

`CancellationTokenSource` is created once in `Initialize()` and cancelled in `Dispose()`. All `Task.Run` lambdas check the token before re-enqueueing. `TaskCanceledException` is caught and silently swallowed — this is expected behaviour during shutdown.

---

## Firebase Reporting: Compile-Time Guards

The `IFirebaseReporter` interface + `#if FIREBASE_CRASHLYTICS` pattern ensures:
- Project compiles without Firebase SDK (default)
- Adding Firebase requires only a scripting define symbol change
- No runtime cost when disabled (`NullFirebaseReporter` methods are empty)

The reporter is injected in `Initialize()` based on the compile-time flag, not via DI, because:
- Firebase SDK availability is a build-time decision, not a runtime config
- Keeps the DI bindings simple — one fewer thing to configure in `MainGameInstaller`

---

## Editor Window: Fortis Menu vs Tools Menu

The new editor window lives under **Fortis → Analytics Monitor** rather than the existing **Tools → Analytics Monitor**. This:
- Avoids menu item collision
- Groups Fortis-specific tooling under one menu
- Allows both windows to coexist (the Tools window works with any strategy; the Fortis window has PlayerLoop-specific controls like frame budget editing)

---

## No Assembly Definitions

The spec requested `Analytics.Runtime.asmdef` and `Analytics.Editor.asmdef`. This was intentionally skipped because:

1. The existing codebase has **zero** asmdef files — all code compiles into `Assembly-CSharp`
2. An asmdef-based assembly **cannot reference** `Assembly-CSharp` (Unity's restriction)
3. The new code references: `IAnalyticsService`, `CircuitBreaker`, `AnalyticsMetrics`, `AnalyticsConfig`, `DiContainer`, `InjectAttribute`, `IInitializable`, `ITickable`, `IDisposable` — all in `Assembly-CSharp`

If the project later adopts assembly definitions project-wide, the Analytics module is structured to easily receive its own asmdef (the folder hierarchy `Runtime/` and `Editor/` already follows Unity's asmdef conventions).

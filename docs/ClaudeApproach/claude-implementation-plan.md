# Resilient SDK Proxy Tool — Implementation Plan

> **Project:** Fortis-Prompt (Unity 6, URP 17, C# 9.0, .NET 4.7.1)
> **Role:** Staff Client Engineer
> **Created:** 2026-04-09
> **Status:** Ready for implementation

---

## Table of Contents

1. [Context & Problem Statement](#context--problem-statement)
2. [Architecture Overview](#architecture-overview)
3. [Design Decisions](#design-decisions)
4. [Known Risks & Mitigations](#known-risks--mitigations)
5. [Phase 1 — AnalyticsMetrics](#phase-1--analyticsmetrics)
6. [Phase 2 — CircuitBreaker](#phase-2--circuitbreaker)
7. [Phase 3 — ResilientAnalytics](#phase-3--resilientanalytics)
8. [Phase 4 — Editor Window](#phase-4--editor-window)
9. [Phase 5 — Unit Tests](#phase-5--unit-tests)
10. [Phase 6 — Verification & QA](#phase-6--verification--qa)
11. [Appendix A — Prompts Used During Planning](#appendix-a--prompts-used-during-planning)
12. [Appendix B — User Decisions Log](#appendix-b--user-decisions-log)
13. [Appendix C — Code Review Findings](#appendix-c--code-review-findings)

---

## Context & Problem Statement

### The Problem

The `UnstableLegacyService` mock (`Assets/Code/UnstableService.cs`, namespace `Interview.Mocks`) simulates a broken third-party analytics SDK with two critical flaws:

1. **Main-thread hitches**: 20% chance of calling `Thread.Sleep(500)`, blocking the calling thread for 500ms
2. **High failure rate**: 30% chance of throwing an `Exception`

Feature teams are experiencing game hitches whenever analytics events are sent because calls are made synchronously on the main thread.

### The Mock (read-only, do NOT modify)

```csharp
// Assets/Code/UnstableService.cs
namespace Interview.Mocks
{
    public class UnstableLegacyService
    {
        private readonly float _failureRate = 0.3f;
        private readonly int _hitchDurationMs = 500;

        public bool SendEvent(string eventName)
        {
            // 20% chance of 500ms Thread.Sleep
            if (UnityEngine.Random.value < 0.2f)
                Thread.Sleep(_hitchDurationMs);

            // 30% chance of exception
            if (UnityEngine.Random.value < _failureRate)
                throw new Exception("LegacyService internal failure: NullReferenceException at 0x004F");

            Debug.Log($"[LegacySDK] Event '{eventName}' sent successfully.");
            return true;
        }
    }
}
```

### Deliverables

1. A `ResilientAnalytics` class that wraps the provided `UnstableService`
2. Implementation of a **Circuit Breaker** to protect the game from constant failures
3. A small **Unity Editor Tool** that shows the current status of the SDK (Closed, Open, Half-Open) and the total "saved" hitch time

---

## Architecture Overview

```
Main Thread                                  Background Thread
┌────────────────────┐                      ┌──────────────────────────┐
│  ResilientAnalytics│                      │      Worker Loop          │
│  (MonoBehaviour)   │                      │                          │
│                    │   ConcurrentQueue    │  1. Dequeue event        │
│  SendEvent(name)───┼──────enqueue────────>│  2. Check CircuitBreaker │
│                    │                      │  3. Call SendEvent()     │
│  Update()          │   mainThreadQueue    │  4. Record metrics       │
│  (drain callbacks)─┼<─────dispatch────────│  5. Buffer if Open       │
│                    │                      │                          │
└────────────────────┘                      │  Retry Buffer (max 100)  │
         │                                  │  Flushed on recovery     │
         v                                  └──────────────────────────┘
┌────────────────────┐
│  EditorWindow      │
│  (reads Metrics)   │
│  Tools > Resilient │
│  Analytics Monitor │
└────────────────────┘
```

### File Layout

```
Assets/Code/
├── UnstableService.cs                      # EXISTING — DO NOT MODIFY
├── AnalyticsMetrics.cs                     # Phase 1 — lock-free counters
├── CircuitBreaker.cs                       # Phase 2 — state machine
├── ResilientAnalytics.cs                   # Phase 3 — MonoBehaviour singleton + worker thread
├── Editor/
│   └── ResilientAnalyticsWindow.cs         # Phase 4 — IMGUI EditorWindow
Assets/Tests/
├── EditMode/
│   ├── EditModeTests.asmdef                # Phase 5 — assembly definition
│   ├── CircuitBreakerTests.cs              # Phase 5 — circuit breaker unit tests
│   └── AnalyticsMetricsTests.cs            # Phase 5 — metrics unit tests
```

### Namespace

All new code uses namespace `Fortis.Analytics`. Editor code uses `Fortis.Analytics.Editor`.

---

## Design Decisions

| # | Decision | Choice | Rationale |
|---|----------|--------|-----------|
| 1 | **Threading model** | Dedicated background thread with `ConcurrentQueue` | A single long-lived thread avoids ThreadPool starvation. Analytics events are fire-and-forget — they don't need parallel execution. Serialized calls simplify circuit breaker logic since there are no race conditions on state transitions. `Task.Run` per event would create unbounded concurrency against a service with 500ms hitches. |
| 2 | **Circuit breaker** | Separate class (`CircuitBreaker`) | Single Responsibility Principle. Independently testable. Reusable for other unstable SDKs. |
| 3 | **Thread safety** | `Interlocked` operations everywhere, no locks | Lock-free approach for low contention (single writer thread, readers on main thread). `Interlocked.CompareExchange` for atomic state transitions. |
| 4 | **Open-circuit behavior** | Bounded retry buffer (max 100 events) | Events queued during Open state are replayed when circuit recovers. Oldest events dropped if buffer fills. Prevents unbounded memory growth during prolonged outages. |
| 5 | **Circuit thresholds** | 5 consecutive failures → Open, 10s cooldown → Half-Open | Balances protection (stops hammering after 5 failures) with recovery speed (probes after 10 seconds). |
| 6 | **Retry in Closed state** | None | With 30% failure rate, retrying every failure amplifies load. The circuit breaker's job is to detect degradation. Individual event loss is acceptable for analytics telemetry. |
| 7 | **Singleton pattern** | MonoBehaviour + `DontDestroyOnLoad` | Needs `Update()` for main-thread callback dispatch and `OnDestroy()` for worker thread cleanup. Static classes cannot participate in the Unity lifecycle. |
| 8 | **Public API** | `SendEvent(string name, Action<bool> onComplete = null)` | Fire-and-forget by default. Optional callback dispatched on the main thread for callers that need status. |
| 9 | **`UnityEngine.Random` on bg thread** | Call from background thread with documented risk and fallback plan | `UnityEngine.Random` is documented as main-thread-only. Behavior from background threads varies by Unity patch version (may return 0, may throw `UnityException`). We call from bg thread because dispatching back to main thread negates the hitch-avoidance goal. The catch block in the worker loop handles `UnityException` gracefully. **See Known Risks section for fallback strategy.** |
| 10 | **Unit tests** | EditMode tests for CircuitBreaker and AnalyticsMetrics | These are pure C# classes with no Unity lifecycle dependencies — perfect for EditMode tests. ResilientAnalytics requires PlayMode (MonoBehaviour lifecycle) which is more complex and verified manually. |
| 11 | **Worker thread signaling** | `AutoResetEvent` (not `ManualResetEventSlim`) | `AutoResetEvent.WaitOne()` atomically signals and resets — no gap for lost signals between `Wait()` and `Reset()`. Standard pattern for producer-consumer wake-ups. |
| 12 | **Time abstraction in CircuitBreaker** | Injectable `Func<long>` for timestamp | Allows deterministic testing without `Thread.Sleep`. Defaults to `DateTime.UtcNow.Ticks` in production. |

---

## Known Risks & Mitigations

### Risk 1: `UnityEngine.Random.value` from background thread

**Severity:** High
**Component:** `UnstableLegacyService.SendEvent()` (called from worker thread)

**Problem:** The mock calls `UnityEngine.Random.value` internally, which is documented as main-thread-only. From a background thread, behavior varies by Unity version:
- Some builds silently return 0 (mock never hitches, never fails — circuit breaker becomes untestable)
- Some builds throw `UnityException`
- Some builds work correctly with per-thread state

**Mitigation strategy (ordered by preference):**
1. **Verify during Phase 6:** Add an explicit test in QA to confirm `UnityEngine.Random.value` produces non-zero values from a background thread in Unity 6000.0.60f1. If it works, document the verified version.
2. **If Random returns 0:** The mock always succeeds and never hitches. The proxy still works correctly (events processed off main thread), but the circuit breaker cannot be stress-tested via the mock. Document this and test the circuit breaker exclusively via unit tests.
3. **If Random throws `UnityException`:** The existing catch block in `ExecuteEvent` handles it. Every call fails → circuit breaker trips to Open → events buffered → periodic Half-Open probes continue to fail → system degrades gracefully. The proxy is still protecting the main thread.
4. **Fallback (if none of the above are acceptable):** Wrap SDK calls via a bounded main-thread dispatcher using a coroutine with `WaitForEndOfFrame` and a time budget per frame (e.g., 2ms). This preserves `Random.value` correctness at the cost of limited throughput — but the hitch is bounded to the time budget, not the 500ms `Thread.Sleep`.

**Decision:** Proceed with background thread approach. The catch block provides safety. Verify behavior in Phase 6 and document the result.

### Risk 2: Domain reload during Play Mode exit

**Severity:** Medium
**Component:** `ResilientAnalytics.Shutdown()`

**Problem:** Unity's domain reload destroys MonoBehaviours and reloads assemblies. If the worker thread doesn't exit within the `Join` timeout, it may reference disposed objects.

**Mitigation:** `Thread.IsBackground = true` ensures the CLR kills the thread on domain unload. `Shutdown()` only disposes `AutoResetEvent` after confirming the thread has exited via `Join`. If `Join` times out, the handle is intentionally leaked (the CLR finalizer will clean it up).

---

## Phase 1 — AnalyticsMetrics

**File:** `Assets/Code/AnalyticsMetrics.cs`
**Namespace:** `Fortis.Analytics`
**Dependencies:** None
**Estimated effort:** Small

### Purpose

A lock-free, thread-safe container for all operational counters. Shared between the worker thread (writes) and the editor window (reads).

### Class Design

```csharp
using System.Threading;

namespace Fortis.Analytics
{
    public class AnalyticsMetrics
    {
        // --- Private fields (all long for Interlocked compatibility) ---
        private long _totalEnqueued;
        private long _totalSucceeded;
        private long _totalFailed;
        private long _totalDropped;        // dropped because retry buffer was full
        private long _totalRetried;        // successfully replayed from retry buffer
        private long _savedHitchTimeMs;    // cumulative ms of work moved off main thread
        private long _circuitOpenCount;    // number of times breaker tripped to Open
        private long _retryBufferSize;     // current items in retry buffer

        // --- Public read properties ---
        // Use Interlocked.Read for 64-bit atomicity on 32-bit platforms
        public long TotalEnqueued    => Interlocked.Read(ref _totalEnqueued);
        public long TotalSucceeded   => Interlocked.Read(ref _totalSucceeded);
        public long TotalFailed      => Interlocked.Read(ref _totalFailed);
        public long TotalDropped     => Interlocked.Read(ref _totalDropped);
        public long TotalRetried     => Interlocked.Read(ref _totalRetried);
        public long SavedHitchTimeMs => Interlocked.Read(ref _savedHitchTimeMs);
        public long CircuitOpenCount => Interlocked.Read(ref _circuitOpenCount);
        public long RetryBufferSize  => Interlocked.Read(ref _retryBufferSize);

        // --- Computed properties ---
        // Note: Not atomic across both reads. The worker thread may update
        // one counter between reads. Acceptable for display purposes only.
        public float SuccessRate
        {
            get
            {
                var succeeded = TotalSucceeded;
                var failed = TotalFailed;
                var total = succeeded + failed;
                return total == 0 ? 1f : (float)succeeded / total;
            }
        }

        // --- Increment methods (called from worker thread) ---
        public void RecordEnqueue()                 => Interlocked.Increment(ref _totalEnqueued);
        public void RecordSuccess()                 => Interlocked.Increment(ref _totalSucceeded);
        public void RecordFailure()                 => Interlocked.Increment(ref _totalFailed);
        public void RecordDrop()                    => Interlocked.Increment(ref _totalDropped);
        public void RecordRetry()                   => Interlocked.Increment(ref _totalRetried);
        public void RecordCircuitOpen()             => Interlocked.Increment(ref _circuitOpenCount);
        public void AddSavedHitchTime(long ms)      => Interlocked.Add(ref _savedHitchTimeMs, ms);
        public void SetRetryBufferSize(long size)    => Interlocked.Exchange(ref _retryBufferSize, size);

        // --- Reset (for editor tooling / development iteration) ---
        public void Reset()
        {
            Interlocked.Exchange(ref _totalEnqueued, 0);
            Interlocked.Exchange(ref _totalSucceeded, 0);
            Interlocked.Exchange(ref _totalFailed, 0);
            Interlocked.Exchange(ref _totalDropped, 0);
            Interlocked.Exchange(ref _totalRetried, 0);
            Interlocked.Exchange(ref _savedHitchTimeMs, 0);
            Interlocked.Exchange(ref _circuitOpenCount, 0);
            Interlocked.Exchange(ref _retryBufferSize, 0);
        }
    }
}
```

### Key Notes for Engineers

- **Why `long` instead of `int`?** `Interlocked.Read` only provides atomic reads for 64-bit values. On 32-bit platforms, reading a `long` without `Interlocked.Read` can produce torn reads.
- **`SavedHitchTimeMs` explained:** Every time an event is processed on the background thread, we add the wall-clock elapsed time of that call. This represents time that *would have* blocked the main thread. If the SDK's `Thread.Sleep(500)` fires, we capture those 500ms. Even fast successful calls contribute their measured duration.
- **Reset method:** `Reset()` uses `Interlocked.Exchange` per field. Not atomic across all fields (a reader may see partially-reset state), but acceptable for a development tool. Exposed in the editor window as a "Reset Metrics" button.

### Acceptance Criteria

- [ ] All counters start at 0
- [ ] Increment methods are thread-safe (no torn writes)
- [ ] Read properties return consistent atomic values
- [ ] `SuccessRate` returns 1.0 when no events have been processed
- [ ] `SuccessRate` computes correctly after mixed success/failure

---

## Phase 2 — CircuitBreaker

**File:** `Assets/Code/CircuitBreaker.cs`
**Namespace:** `Fortis.Analytics`
**Dependencies:** None
**Estimated effort:** Medium

### Purpose

A standalone, reusable circuit breaker with three states. Thread-safe via `Interlocked.CompareExchange` for state transitions. No locks.

### State Machine

```
         success
    ┌──────────────────┐
    │                  │
    v     N failures   │
 CLOSED ──────────> OPEN
    ^                 │
    │    timeout      │
    │    elapsed      v
    └──── success ── HALF-OPEN
          failure ──> OPEN (re-trip)
```

### Class Design

```csharp
using System;
using System.Threading;

namespace Fortis.Analytics
{
    public enum CircuitState
    {
        Closed,     // Normal operation — all requests pass through
        Open,       // Failures exceeded threshold — requests blocked
        HalfOpen    // Cooldown elapsed — one probe request allowed
    }

    public class CircuitBreaker
    {
        // --- Configuration (immutable after construction) ---
        public int FailureThreshold { get; }     // consecutive failures to trip (default: 5)
        public int OpenDurationMs { get; }       // ms to wait before Half-Open probe (default: 10000)

        // --- Thread-safe state ---
        private int _state;                      // CircuitState cast to int for Interlocked
        private int _consecutiveFailures;
        private long _openedAtTicks;             // timestamp ticks when tripped to Open
        private readonly long _openDurationTicks; // cached conversion to avoid per-call allocation
        private readonly Func<long> _getTimestamp; // injectable for deterministic testing

        // --- Events ---
        // Called when state changes. Invoked from the worker thread — do NOT call Unity APIs here.
        public event Action<CircuitState> OnStateChanged;

        // --- Public state read ---
        public CircuitState State =>
            (CircuitState)Interlocked.CompareExchange(ref _state, 0, 0);

        // --- Constructor ---
        /// <param name="failureThreshold">Consecutive failures before tripping (must be >= 1)</param>
        /// <param name="openDurationMs">Milliseconds to wait before Half-Open probe (must be >= 0)</param>
        /// <param name="getTimestamp">Optional time source for testing. Defaults to DateTime.UtcNow.Ticks</param>
        public CircuitBreaker(int failureThreshold = 5, int openDurationMs = 10000,
                              Func<long> getTimestamp = null)
        {
            if (failureThreshold < 1)
                throw new ArgumentOutOfRangeException(nameof(failureThreshold), "Must be >= 1");
            if (openDurationMs < 0)
                throw new ArgumentOutOfRangeException(nameof(openDurationMs), "Must be >= 0");

            FailureThreshold = failureThreshold;
            OpenDurationMs = openDurationMs;
            _openDurationTicks = TimeSpan.FromMilliseconds(openDurationMs).Ticks;
            _getTimestamp = getTimestamp ?? (() => DateTime.UtcNow.Ticks);
            _state = (int)CircuitState.Closed;
        }

        // --- Core API ---

        /// <summary>
        /// Returns true if the circuit allows a request to pass through.
        /// Handles Open → HalfOpen transition when timeout elapses.
        /// </summary>
        public bool AllowRequest()
        {
            var state = State;

            if (state == CircuitState.Closed)
                return true;

            if (state == CircuitState.Open)
            {
                var elapsed = _getTimestamp() - Interlocked.Read(ref _openedAtTicks);
                if (elapsed >= _openDurationTicks)
                {
                    // Attempt transition to HalfOpen (CAS prevents races)
                    if (TryTransition(CircuitState.Open, CircuitState.HalfOpen))
                    {
                        RaiseStateChanged(CircuitState.HalfOpen);
                        return true;
                    }
                }
                return false; // Still in Open, timeout not elapsed
            }

            // HalfOpen: allow one probe request
            return true;
        }

        /// <summary>
        /// Record a successful call. Resets failure count.
        /// In HalfOpen state, transitions back to Closed.
        /// </summary>
        public void RecordSuccess()
        {
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            if (TryTransition(CircuitState.HalfOpen, CircuitState.Closed))
            {
                RaiseStateChanged(CircuitState.Closed);
            }
        }

        /// <summary>
        /// Record a failed call. Increments consecutive failure count.
        /// Trips to Open if threshold reached or if currently in HalfOpen.
        /// </summary>
        public void RecordFailure()
        {
            var failures = Interlocked.Increment(ref _consecutiveFailures);

            if (State == CircuitState.HalfOpen)
            {
                Trip(); // Any failure in HalfOpen re-trips immediately
            }
            else if (failures >= FailureThreshold)
            {
                Trip();
            }
        }

        // --- Private helpers ---

        private void Trip()
        {
            var prev = (CircuitState)Interlocked.Exchange(ref _state, (int)CircuitState.Open);
            Interlocked.Exchange(ref _openedAtTicks, _getTimestamp());

            if (prev != CircuitState.Open) // Only fire event on actual transition
                RaiseStateChanged(CircuitState.Open);
        }

        /// <summary>
        /// Safe event invocation — prevents subscriber exceptions from crashing the worker thread.
        /// </summary>
        private void RaiseStateChanged(CircuitState newState)
        {
            try { OnStateChanged?.Invoke(newState); }
            catch (Exception ex) { UnityEngine.Debug.LogException(ex); }
        }

        private bool TryTransition(CircuitState from, CircuitState to)
        {
            return Interlocked.CompareExchange(ref _state, (int)to, (int)from) == (int)from;
        }
    }
}
```

### Key Notes for Engineers

- **Why `Interlocked.CompareExchange` for state transitions?** This is a Compare-And-Swap (CAS) operation. It atomically checks if the current state equals `from` and only then sets it to `to`. This prevents two threads from both transitioning the state (e.g., two threads trying Open→HalfOpen).
- **Why consecutive failures, not time-windowed?** The mock has a constant 30% failure rate — a time window adds complexity for no benefit. Consecutive failures accurately detect "the service is in a bad streak."
- **`OnStateChanged` threading warning:** This event fires from the worker thread. Subscribers must not call Unity main-thread APIs. The `RaiseStateChanged` wrapper catches subscriber exceptions to prevent crashing the worker thread.
- **Injectable `Func<long> getTimestamp`:** Defaults to `DateTime.UtcNow.Ticks` in production. Tests inject a controllable clock to advance time deterministically — no `Thread.Sleep` in tests, no flaky CI.
- **Cached `_openDurationTicks`:** The `TimeSpan.FromMilliseconds()` conversion is computed once in the constructor instead of on every `AllowRequest()` call.
- **Parameter validation:** Constructor throws `ArgumentOutOfRangeException` for invalid thresholds (< 1) or durations (< 0). Fail fast on misconfiguration.

### Acceptance Criteria

- [ ] Starts in `Closed` state
- [ ] `AllowRequest()` returns `true` in Closed state
- [ ] After `FailureThreshold` consecutive failures, state transitions to `Open`
- [ ] `AllowRequest()` returns `false` in Open state before timeout
- [ ] After `OpenDurationMs` elapses, `AllowRequest()` transitions to `HalfOpen` and returns `true`
- [ ] A success in `HalfOpen` transitions to `Closed`
- [ ] A failure in `HalfOpen` transitions back to `Open` immediately
- [ ] `RecordSuccess()` resets consecutive failure count
- [ ] `OnStateChanged` fires on each transition (not duplicate fires)
- [ ] Thread-safe under concurrent access

---

## Phase 3 — ResilientAnalytics

**File:** `Assets/Code/ResilientAnalytics.cs`
**Namespace:** `Fortis.Analytics`
**Dependencies:** Phase 1 (`AnalyticsMetrics`), Phase 2 (`CircuitBreaker`), `Interview.Mocks.UnstableLegacyService`
**Estimated effort:** Large

### Purpose

The public-facing API for feature teams. A MonoBehaviour singleton that manages a dedicated background worker thread, event queuing, circuit breaker integration, retry buffering, and main-thread callback dispatch.

### Internal Event Struct

```csharp
private readonly struct AnalyticsEvent
{
    public readonly string EventName;
    public readonly Action<bool> Callback; // nullable

    public AnalyticsEvent(string eventName, Action<bool> callback)
    {
        EventName = eventName;
        Callback = callback;
    }
}
```

### Class Design

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Interview.Mocks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Fortis.Analytics
{
    public class ResilientAnalytics : MonoBehaviour
    {
        // --- Singleton ---
        private static ResilientAnalytics _instance;
        public static ResilientAnalytics Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("[ResilientAnalytics]");
                    _instance = go.AddComponent<ResilientAnalytics>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        // --- Configuration (tweakable in Inspector) ---
        [Header("Circuit Breaker")]
        [SerializeField] private int _failureThreshold = 5;
        [SerializeField] private int _circuitOpenDurationMs = 10000;

        [Header("Retry Buffer")]
        [SerializeField] private int _maxRetryBufferSize = 100;

        // --- Public accessors ---
        public CircuitBreaker CircuitBreaker { get; private set; }
        public AnalyticsMetrics Metrics { get; private set; }

        // --- Public state ---
        /// <summary>
        /// Returns true if the circuit is Closed or HalfOpen (SDK accepting events).
        /// Feature teams can use this to skip expensive payload construction when SDK is down.
        /// </summary>
        public bool IsReady => CircuitBreaker?.State != CircuitState.Open;
        public int MaxRetryBufferSize => _maxRetryBufferSize;

        // --- Private state ---
        private UnstableLegacyService _service;
        private ConcurrentQueue<AnalyticsEvent> _eventQueue;
        private ConcurrentQueue<Action> _mainThreadActions;
        private Queue<AnalyticsEvent> _retryBuffer;   // accessed only from worker thread
        private Thread _workerThread;
        private volatile bool _shutdownRequested;
        private volatile bool _flushRequested;         // decoupled flush signal (set by callback, consumed by worker)
        private AutoResetEvent _workAvailable;         // atomically resets on WaitOne — no lost signals

        // --- Public API ---

        /// <summary>
        /// Enqueue an analytics event. Non-blocking, thread-safe.
        /// Optional callback is dispatched on the main thread.
        /// </summary>
        public void SendEvent(string eventName, Action<bool> onComplete = null)
        {
            Metrics.RecordEnqueue();
            _eventQueue.Enqueue(new AnalyticsEvent(eventName, onComplete));
            _workAvailable.Set(); // wake worker
        }

        // --- Unity Lifecycle ---

        private void Awake()
        {
            // Enforce singleton
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);

            // Initialize collections here (not field initializers) to avoid
            // allocating disposable resources for destroyed singleton duplicates
            _eventQueue = new ConcurrentQueue<AnalyticsEvent>();
            _mainThreadActions = new ConcurrentQueue<Action>();
            _retryBuffer = new Queue<AnalyticsEvent>();
            _workAvailable = new AutoResetEvent(false);

            // Initialize dependencies
            _service = new UnstableLegacyService();
            Metrics = new AnalyticsMetrics();
            CircuitBreaker = new CircuitBreaker(_failureThreshold, _circuitOpenDurationMs);

            CircuitBreaker.OnStateChanged += OnCircuitStateChanged;

            // Start worker thread
            _workerThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "ResilientAnalytics-Worker"
            };
            _workerThread.Start();

            Debug.Log("[ResilientAnalytics] Initialized.");
        }

        private void Update()
        {
            // Drain main-thread callback queue
            while (_mainThreadActions.TryDequeue(out var action))
            {
                try { action?.Invoke(); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        private void OnDestroy()
        {
            Shutdown();
        }

        private void OnApplicationQuit()
        {
            Shutdown();
        }

        // --- Worker Thread ---

        private void WorkerLoop()
        {
            while (!_shutdownRequested)
            {
                // AutoResetEvent.WaitOne atomically resets — no lost signals
                _workAvailable.WaitOne(200); // timeout so we can check shutdown
                ProcessEventQueue();
            }
        }

        private void ProcessEventQueue()
        {
            while (_eventQueue.TryDequeue(out var evt))
            {
                if (_shutdownRequested) break;

                if (!CircuitBreaker.AllowRequest())
                {
                    // Circuit is Open — buffer for retry
                    BufferForRetry(evt);
                    continue;
                }

                ExecuteEvent(evt);
            }

            // Flush retry buffer AFTER processing queue (not re-entrantly during callbacks)
            if (_flushRequested)
            {
                _flushRequested = false;
                FlushRetryBuffer();
            }
        }

        private void ExecuteEvent(AnalyticsEvent evt)
        {
            var sw = Stopwatch.StartNew();
            bool success = false;

            try
            {
                success = _service.SendEvent(evt.EventName);
                sw.Stop();
                CircuitBreaker.RecordSuccess();
                Metrics.RecordSuccess();
            }
            catch (Exception ex)
            {
                sw.Stop();
                CircuitBreaker.RecordFailure();
                Metrics.RecordFailure();
                Debug.LogWarning($"[ResilientAnalytics] Event '{evt.EventName}' failed: {ex.Message}");
            }

            Metrics.AddSavedHitchTime(sw.ElapsedMilliseconds);
            DispatchCallback(evt.Callback, success);
        }

        private void BufferForRetry(AnalyticsEvent evt)
        {
            // Notify caller immediately that event was deferred (callback receives false).
            // The event itself is retried without a callback to avoid double-invocation.
            DispatchCallback(evt.Callback, false);

            if (_retryBuffer.Count >= _maxRetryBufferSize)
            {
                // Drop oldest event to make room
                _retryBuffer.Dequeue();
                Metrics.RecordDrop();
            }
            _retryBuffer.Enqueue(new AnalyticsEvent(evt.EventName, null));
            Metrics.SetRetryBufferSize(_retryBuffer.Count);
        }

        private void FlushRetryBuffer()
        {
            while (_retryBuffer.Count > 0)
            {
                var evt = _retryBuffer.Dequeue();
                _eventQueue.Enqueue(evt);
                Metrics.RecordRetry();
            }
            Metrics.SetRetryBufferSize(0);
            _workAvailable.Set(); // wake worker to process re-enqueued events
        }

        // --- Helpers ---

        private void OnCircuitStateChanged(CircuitState newState)
        {
            if (newState == CircuitState.Open)
                Metrics.RecordCircuitOpen();

            // Signal flush — do NOT call FlushRetryBuffer() here.
            // This callback fires from inside CircuitBreaker.RecordSuccess(),
            // which is called from ExecuteEvent(), which is called from
            // ProcessEventQueue(). Flushing here would re-entrantly mutate
            // the queue being iterated. Instead, set a flag and let
            // ProcessEventQueue() flush after its loop completes.
            if (newState == CircuitState.Closed)
                _flushRequested = true;

            Debug.Log($"[ResilientAnalytics] Circuit state → {newState}");
        }

        private void DispatchCallback(Action<bool> callback, bool result)
        {
            if (callback != null)
                _mainThreadActions.Enqueue(() => callback(result));
        }

        private void Shutdown()
        {
            if (_shutdownRequested) return;
            _shutdownRequested = true;
            _workAvailable.Set(); // wake worker so it exits

            bool threadExited = false;
            if (_workerThread != null && _workerThread.IsAlive)
                threadExited = _workerThread.Join(2000); // bounded wait

            // Only dispose if thread has exited — prevents ObjectDisposedException
            // if Join timed out. IsBackground=true ensures CLR kills it on domain unload.
            if (threadExited || _workerThread == null || !_workerThread.IsAlive)
                _workAvailable?.Dispose();

            if (CircuitBreaker != null)
                CircuitBreaker.OnStateChanged -= OnCircuitStateChanged;

            Debug.Log("[ResilientAnalytics] Shut down.");
        }
    }
}
```

### Key Notes for Engineers

- **Why `AutoResetEvent` instead of `ManualResetEventSlim`?** `AutoResetEvent.WaitOne()` atomically signals and resets in one operation. With `ManualResetEventSlim`, a gap between `Wait()` and `Reset()` can lose signals from `SendEvent()` on the main thread, adding up to 200ms latency. `AutoResetEvent` eliminates this race entirely.
- **Decoupled retry buffer flush.** `OnCircuitStateChanged` sets `_flushRequested = true` instead of calling `FlushRetryBuffer()` directly. The flush happens at the end of `ProcessEventQueue()`, after the dequeue loop completes. This avoids re-entrant mutation of `_eventQueue` during iteration — a fragile pattern that could cause infinite loops if future logic is added to the callback.
- **Immediate callback on buffer.** When an event is buffered (circuit Open), the caller's callback is dispatched immediately with `false`. The event is re-enqueued without a callback to prevent double-invocation on replay. Callers get immediate feedback instead of silently deferred callbacks.
- **`IsReady` property.** Returns `false` when circuit is Open. Feature teams can use this to skip expensive payload construction when the SDK is known to be down.
- **Retry buffer is only accessed from the worker thread.** No synchronization needed on `_retryBuffer` — it's a plain `Queue<T>`.
- **Field initialization in `Awake()`, not field initializers.** Prevents allocating `AutoResetEvent` (kernel handle) and collections for destroyed singleton duplicates.
- **`UnityEngine.Random.value` concern:** See "Known Risks" section. The catch block in `ExecuteEvent` handles `UnityException` gracefully.
- **`Stopwatch` for timing:** Uses the high-resolution performance counter, more accurate than `DateTime` for elapsed measurement.
- **Shutdown is idempotent and safe.** Both `OnDestroy` and `OnApplicationQuit` may fire. The `_shutdownRequested` flag ensures cleanup runs only once. `AutoResetEvent` is only disposed after confirming the thread exited via `Join` — prevents `ObjectDisposedException` if the join times out.
- **`MaxRetryBufferSize` exposed as public property.** The editor window uses this instead of hardcoding `100` in the progress bar.

### Acceptance Criteria

- [ ] `SendEvent()` returns immediately (non-blocking on main thread)
- [ ] Events are processed on the background thread
- [ ] No main-thread hitches during event processing (verify with Profiler)
- [ ] Circuit breaker trips after 5 consecutive failures
- [ ] Events are buffered when circuit is Open
- [ ] Buffer is capped at 100 — oldest dropped if full
- [ ] Buffer is flushed when circuit returns to Closed
- [ ] Optional callbacks are dispatched on the main thread
- [ ] Clean shutdown on play mode exit (no orphaned threads)
- [ ] Singleton survives scene loads (`DontDestroyOnLoad`)
- [ ] Inspector-configurable thresholds

---

## Phase 4 — Editor Window

**File:** `Assets/Code/Editor/ResilientAnalyticsWindow.cs`
**Namespace:** `Fortis.Analytics.Editor`
**Dependencies:** Phase 3 (`ResilientAnalytics`)
**Estimated effort:** Medium

### Purpose

An IMGUI `EditorWindow` providing real-time visibility into the SDK proxy's health, circuit breaker state, and performance metrics during play mode.

### Class Design

```csharp
using UnityEditor;
using UnityEngine;

namespace Fortis.Analytics.Editor
{
    public class ResilientAnalyticsWindow : EditorWindow
    {
        [MenuItem("Tools/Resilient Analytics Monitor")]
        public static void ShowWindow()
        {
            GetWindow<ResilientAnalyticsWindow>("Analytics Monitor");
        }

        private void OnGUI()
        {
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Enter Play Mode to view analytics metrics.",
                    MessageType.Info);
                return;
            }

            var instance = ResilientAnalytics.Instance;
            if (instance == null) return;

            var breaker = instance.CircuitBreaker;
            var metrics = instance.Metrics;

            // --- Circuit Breaker State ---
            EditorGUILayout.LabelField("Circuit Breaker", EditorStyles.boldLabel);
            var state = breaker.State;
            var prevColor = GUI.color;
            GUI.color = state switch
            {
                CircuitState.Closed  => Color.green,
                CircuitState.Open    => Color.red,
                CircuitState.HalfOpen => Color.yellow,
                _                    => Color.white
            };
            EditorGUILayout.LabelField("State", state.ToString(), EditorStyles.boldLabel);
            GUI.color = prevColor;

            EditorGUILayout.Space(10);

            // --- Metrics (read-only via disabled group) ---
            EditorGUILayout.LabelField("Event Metrics", EditorStyles.boldLabel);
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.LongField("Total Enqueued", metrics.TotalEnqueued);
            EditorGUILayout.LongField("Succeeded", metrics.TotalSucceeded);
            EditorGUILayout.LongField("Failed", metrics.TotalFailed);
            EditorGUILayout.LongField("Dropped (buffer full)", metrics.TotalDropped);
            EditorGUILayout.LongField("Retried (replayed)", metrics.TotalRetried);
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space(10);

            // --- Performance (hero section) ---
            EditorGUILayout.LabelField("Performance", EditorStyles.boldLabel);
            var savedMs = metrics.SavedHitchTimeMs;
            var savedDisplay = savedMs >= 1000
                ? $"{savedMs / 1000f:F1}s"
                : $"{savedMs}ms";
            EditorGUILayout.LabelField("Saved Hitch Time", savedDisplay);
            EditorGUILayout.LabelField("Success Rate",
                $"{metrics.SuccessRate * 100f:F1}%");
            EditorGUILayout.LabelField("Circuit Opens",
                metrics.CircuitOpenCount.ToString());
            EditorGUILayout.LabelField("SDK Ready",
                instance.IsReady ? "Yes" : "No (circuit open)");

            EditorGUILayout.Space(10);

            // --- Retry Buffer ---
            EditorGUILayout.LabelField("Retry Buffer", EditorStyles.boldLabel);
            var bufferSize = metrics.RetryBufferSize;
            var maxBuffer = instance.MaxRetryBufferSize;
            var barRect = EditorGUILayout.GetControlRect(false, 20);
            EditorGUI.ProgressBar(barRect,
                maxBuffer > 0 ? bufferSize / (float)maxBuffer : 0f,
                $"{bufferSize} / {maxBuffer}");

            EditorGUILayout.Space(10);

            // --- Test Controls ---
            EditorGUILayout.LabelField("Test Controls", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Send Test Event"))
            {
                instance.SendEvent("editor_test_event", success =>
                    Debug.Log($"[AnalyticsMonitor] Test event result: {success}"));
            }
            if (GUILayout.Button("Send 20 Events (Burst)"))
            {
                for (int i = 0; i < 20; i++)
                    instance.SendEvent($"burst_event_{i}");
            }
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Reset Metrics"))
            {
                metrics.Reset();
            }

            // Force repaint for live updates
            Repaint();
        }

        private void OnInspectorUpdate()
        {
            if (Application.isPlaying)
                Repaint();
        }
    }
}
```

### UI Layout

```
┌─────────────────────────────────────────┐
│ Analytics Monitor                    [x] │
├─────────────────────────────────────────┤
│ Circuit Breaker                         │
│ State:  ██ CLOSED ██  (green)           │
│                                         │
│ Event Metrics                           │
│ Total Enqueued:     150                 │
│ Succeeded:          102                 │
│ Failed:              38                 │
│ Dropped:              3                 │
│ Retried:              7                 │
│                                         │
│ Performance                             │
│ Saved Hitch Time:   12.5s              │
│ Success Rate:       72.9%              │
│ Circuit Opens:      3                   │
│                                         │
│ Retry Buffer                            │
│ [████████░░░░░░░░░░░░] 8 / 100         │
│                                         │
│ Test Controls                           │
│ [Send Test Event] [Send 20 Events]      │
└─────────────────────────────────────────┘
```

### Key Notes for Engineers

- **`Repaint()` in `OnGUI`:** Ensures smooth updates while the window is focused. `OnInspectorUpdate` fires ~10Hz for updates when the window is not focused.
- **`EditorGUILayout.LongField` inside `BeginDisabledGroup(true)`:** `LongField` is editable by default. Wrapping in a disabled group makes it read-only and visually grayed out — correct for display metrics.
- **ProgressBar uses `MaxRetryBufferSize`:** Reads the configurable max from `ResilientAnalytics` instead of hardcoding `100`. If the value is changed via Inspector, the bar scales correctly.
- **`IsReady` display:** Shows feature teams at a glance whether the SDK is accepting events.
- **Reset Metrics button:** Calls `AnalyticsMetrics.Reset()` for development iteration without restarting play mode.
- **No play-mode guard on `Instance`:** Accessing `ResilientAnalytics.Instance` during play mode is safe. The early return for `!Application.isPlaying` prevents accidental singleton creation in edit mode.

### Acceptance Criteria

- [ ] Window opens from **Tools > Resilient Analytics Monitor**
- [ ] Shows "Enter Play Mode" help box when not playing
- [ ] Circuit state is color-coded (green/red/yellow)
- [ ] All metrics update in real-time during play mode
- [ ] "Saved Hitch Time" displays in ms or seconds as appropriate
- [ ] Retry buffer progress bar reflects current buffer size
- [ ] "Send Test Event" button enqueues an event and logs callback result
- [ ] "Send 20 Events (Burst)" button triggers circuit breaker behavior

---

## Phase 5 — Unit Tests

**Files:**
- `Assets/Tests/EditMode/EditModeTests.asmdef`
- `Assets/Tests/EditMode/CircuitBreakerTests.cs`
- `Assets/Tests/EditMode/AnalyticsMetricsTests.cs`

**Dependencies:** Phase 1, Phase 2
**Estimated effort:** Medium

### Assembly Definition

```json
// Assets/Tests/EditMode/EditModeTests.asmdef
{
    "name": "EditModeTests",
    "rootNamespace": "Fortis.Analytics.Tests",
    "references": [],
    "includePlatforms": ["Editor"],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": true,
    "precompiledReferences": ["nunit.framework.dll"],
    "autoReferenced": false,
    "defineConstraints": ["UNITY_INCLUDE_TESTS"],
    "optionalUnityReferences": ["TestAssemblies"]
}
```

**Important:** Since `CircuitBreaker` and `AnalyticsMetrics` are in the default `Assembly-CSharp` (no .asmdef), the test assembly definition needs to reference it. In Unity, assemblies without an `.asmdef` are accessible from test assemblies via the `overrideReferences` and `optionalUnityReferences` fields.

### CircuitBreakerTests.cs

```csharp
using System;
using System.Collections.Generic;
using NUnit.Framework;
using Fortis.Analytics;

namespace Fortis.Analytics.Tests
{
    [TestFixture]
    public class CircuitBreakerTests
    {
        // Controllable clock — advances time deterministically. No Thread.Sleep, no flaky CI.
        private long _fakeTicks;
        private long FakeClock() => _fakeTicks;
        private void AdvanceTime(int ms) => _fakeTicks += TimeSpan.FromMilliseconds(ms).Ticks;

        [SetUp]
        public void SetUp()
        {
            _fakeTicks = DateTime.UtcNow.Ticks; // start at a realistic value
        }

        private CircuitBreaker MakeBreaker(int failureThreshold = 5, int openDurationMs = 10000)
        {
            return new CircuitBreaker(failureThreshold, openDurationMs, getTimestamp: FakeClock);
        }

        [Test]
        public void InitialState_IsClosed()
        {
            var cb = MakeBreaker();
            Assert.AreEqual(CircuitState.Closed, cb.State);
        }

        [Test]
        public void AllowRequest_InClosed_ReturnsTrue()
        {
            var cb = MakeBreaker();
            Assert.IsTrue(cb.AllowRequest());
        }

        [Test]
        public void RecordFailure_BelowThreshold_StaysClosed()
        {
            var cb = MakeBreaker(failureThreshold: 5);
            for (int i = 0; i < 4; i++)
                cb.RecordFailure();
            Assert.AreEqual(CircuitState.Closed, cb.State);
        }

        [Test]
        public void RecordFailure_AtThreshold_TransitionsToOpen()
        {
            var cb = MakeBreaker(failureThreshold: 5);
            for (int i = 0; i < 5; i++)
                cb.RecordFailure();
            Assert.AreEqual(CircuitState.Open, cb.State);
        }

        [Test]
        public void AllowRequest_InOpen_ReturnsFalse()
        {
            var cb = MakeBreaker(failureThreshold: 1, openDurationMs: 60000);
            cb.RecordFailure();
            Assert.IsFalse(cb.AllowRequest());
        }

        [Test]
        public void AllowRequest_AfterTimeout_TransitionsToHalfOpen()
        {
            var cb = MakeBreaker(failureThreshold: 1, openDurationMs: 1000);
            cb.RecordFailure(); // trips to Open
            AdvanceTime(1001);  // deterministic — advance past the 1000ms timeout
            Assert.IsTrue(cb.AllowRequest());
            Assert.AreEqual(CircuitState.HalfOpen, cb.State);
        }

        [Test]
        public void AllowRequest_BeforeTimeout_StaysOpen()
        {
            var cb = MakeBreaker(failureThreshold: 1, openDurationMs: 1000);
            cb.RecordFailure(); // trips to Open
            AdvanceTime(500);   // only halfway
            Assert.IsFalse(cb.AllowRequest());
            Assert.AreEqual(CircuitState.Open, cb.State);
        }

        [Test]
        public void RecordSuccess_InHalfOpen_TransitionsToClosed()
        {
            var cb = MakeBreaker(failureThreshold: 1, openDurationMs: 1000);
            cb.RecordFailure();
            AdvanceTime(1001);
            cb.AllowRequest(); // trigger HalfOpen
            cb.RecordSuccess();
            Assert.AreEqual(CircuitState.Closed, cb.State);
        }

        [Test]
        public void RecordFailure_InHalfOpen_TransitionsToOpen()
        {
            var cb = MakeBreaker(failureThreshold: 1, openDurationMs: 1000);
            cb.RecordFailure();
            AdvanceTime(1001);
            cb.AllowRequest(); // trigger HalfOpen
            cb.RecordFailure();
            Assert.AreEqual(CircuitState.Open, cb.State);
        }

        [Test]
        public void RecordSuccess_ResetsConsecutiveFailures()
        {
            var cb = MakeBreaker(failureThreshold: 5);
            for (int i = 0; i < 4; i++)
                cb.RecordFailure();
            cb.RecordSuccess(); // reset
            for (int i = 0; i < 4; i++)
                cb.RecordFailure();
            Assert.AreEqual(CircuitState.Closed, cb.State); // still closed (4 < 5)
        }

        [Test]
        public void OnStateChanged_FiresOnTransition()
        {
            var cb = MakeBreaker(failureThreshold: 1);
            var transitions = new List<CircuitState>();
            cb.OnStateChanged += s => transitions.Add(s);

            cb.RecordFailure(); // → Open
            Assert.AreEqual(1, transitions.Count);
            Assert.AreEqual(CircuitState.Open, transitions[0]);
        }

        [Test]
        public void OnStateChanged_DoesNotFireDuplicateForSameState()
        {
            var cb = MakeBreaker(failureThreshold: 1);
            var transitions = new List<CircuitState>();
            cb.OnStateChanged += s => transitions.Add(s);

            cb.RecordFailure(); // → Open (fires)
            cb.RecordFailure(); // already Open (should NOT fire again)
            Assert.AreEqual(1, transitions.Count);
        }

        [Test]
        public void Constructor_ThrowsOnInvalidThreshold()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new CircuitBreaker(failureThreshold: 0));
        }

        [Test]
        public void Constructor_ThrowsOnNegativeDuration()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new CircuitBreaker(openDurationMs: -1));
        }

        [Test]
        public void FullCycle_Closed_Open_HalfOpen_Closed()
        {
            var cb = MakeBreaker(failureThreshold: 3, openDurationMs: 5000);
            var transitions = new List<CircuitState>();
            cb.OnStateChanged += s => transitions.Add(s);

            // Closed → Open
            cb.RecordFailure();
            cb.RecordFailure();
            cb.RecordFailure();
            Assert.AreEqual(CircuitState.Open, cb.State);

            // Open → HalfOpen (after timeout)
            AdvanceTime(5001);
            Assert.IsTrue(cb.AllowRequest());
            Assert.AreEqual(CircuitState.HalfOpen, cb.State);

            // HalfOpen → Closed (on success)
            cb.RecordSuccess();
            Assert.AreEqual(CircuitState.Closed, cb.State);

            Assert.AreEqual(3, transitions.Count);
            Assert.AreEqual(CircuitState.Open, transitions[0]);
            Assert.AreEqual(CircuitState.HalfOpen, transitions[1]);
            Assert.AreEqual(CircuitState.Closed, transitions[2]);
        }
    }
}
```

### AnalyticsMetricsTests.cs

```csharp
using NUnit.Framework;
using Fortis.Analytics;

namespace Fortis.Analytics.Tests
{
    [TestFixture]
    public class AnalyticsMetricsTests
    {
        [Test]
        public void InitialValues_AreZero()
        {
            var m = new AnalyticsMetrics();
            Assert.AreEqual(0, m.TotalEnqueued);
            Assert.AreEqual(0, m.TotalSucceeded);
            Assert.AreEqual(0, m.TotalFailed);
            Assert.AreEqual(0, m.SavedHitchTimeMs);
        }

        [Test]
        public void RecordSuccess_IncrementsCounter()
        {
            var m = new AnalyticsMetrics();
            m.RecordSuccess();
            m.RecordSuccess();
            Assert.AreEqual(2, m.TotalSucceeded);
        }

        [Test]
        public void SuccessRate_WithNoEvents_Returns1()
        {
            var m = new AnalyticsMetrics();
            Assert.AreEqual(1f, m.SuccessRate);
        }

        [Test]
        public void SuccessRate_ComputesCorrectly()
        {
            var m = new AnalyticsMetrics();
            m.RecordSuccess();
            m.RecordSuccess();
            m.RecordFailure();
            Assert.AreEqual(2f / 3f, m.SuccessRate, 0.001f);
        }

        [Test]
        public void AddSavedHitchTime_Accumulates()
        {
            var m = new AnalyticsMetrics();
            m.AddSavedHitchTime(100);
            m.AddSavedHitchTime(250);
            Assert.AreEqual(350, m.SavedHitchTimeMs);
        }

        [Test]
        public void SetRetryBufferSize_SetsValue()
        {
            var m = new AnalyticsMetrics();
            m.SetRetryBufferSize(42);
            Assert.AreEqual(42, m.RetryBufferSize);
        }

        [Test]
        public void Reset_ZerosAllCounters()
        {
            var m = new AnalyticsMetrics();
            m.RecordEnqueue();
            m.RecordSuccess();
            m.RecordFailure();
            m.RecordDrop();
            m.AddSavedHitchTime(500);
            m.RecordCircuitOpen();
            m.SetRetryBufferSize(10);

            m.Reset();

            Assert.AreEqual(0, m.TotalEnqueued);
            Assert.AreEqual(0, m.TotalSucceeded);
            Assert.AreEqual(0, m.TotalFailed);
            Assert.AreEqual(0, m.TotalDropped);
            Assert.AreEqual(0, m.SavedHitchTimeMs);
            Assert.AreEqual(0, m.CircuitOpenCount);
            Assert.AreEqual(0, m.RetryBufferSize);
        }
    }
}
```

### Acceptance Criteria

- [ ] All tests pass in Unity Test Runner (Window > General > Test Runner > EditMode)
- [ ] Tests cover all circuit breaker state transitions
- [ ] Tests verify metric counter behavior
- [ ] Tests run in < 1 second total

---

## Phase 6 — Verification & QA

### Manual Verification Steps

1. **Open Unity** and open the project
2. **Verify `UnityEngine.Random` bg-thread behavior (CRITICAL — do this first):**
   - Create a temporary test script that calls `UnityEngine.Random.value` from a `Task.Run` and logs the result
   - Confirm it does NOT throw `UnityException` and returns non-zero values
   - If it returns 0 or throws: see Known Risks section for fallback strategies
   - Remove the test script after verification
3. Open **Tools > Resilient Analytics Monitor** — should show "Enter Play Mode" message
5. **Enter Play Mode**
6. Click **"Send Test Event"** — verify:
   - Metrics update (enqueued +1, succeeded or failed +1)
   - No visible hitch in the Editor
   - Console shows callback result if successful
7. Click **"Send 20 Events (Burst)"** — verify:
   - Circuit breaker trips to **Open** (red) after ~5 consecutive failures
   - Retry buffer fills up
   - After 10 seconds, circuit transitions to **HalfOpen** (yellow)
   - On successful probe, transitions to **Closed** (green) and buffer flushes
   - Retried count increments
8. Verify **"Saved Hitch Time"** accumulates continuously
9. Click **"Reset Metrics"** — verify all counters return to 0
10. Open **Unity Profiler** (Window > Analysis > Profiler) during burst sends — verify **no 500ms spikes** on the main thread
11. **Exit Play Mode** — verify clean shutdown (no errors in console, no orphaned threads)
12. **Run unit tests** via Window > General > Test Runner > EditMode > Run All

### Expected Behavior Under Load

With 20 burst events at 30% failure rate:
- ~6 events fail → circuit trips after 5 consecutive failures
- Remaining events go to retry buffer
- After 10s cooldown, one probe event is sent
- If probe succeeds → circuit closes → buffer flushes → events re-process
- If probe fails → circuit re-opens → wait another 10s
- "Saved Hitch Time" should be significant (multiple seconds for 20 events)

---

## Appendix A — Prompts Used During Planning

### Prompt 1: Initial Codebase Exploration

```
Explore the Unity project at /Users/serolo/src/Fortis/Fortis-Prompt thoroughly. I need to understand:

1. The full project structure under Assets/ — what folders exist, what .cs files exist, what .asmdef files exist
2. The Unity project settings — what Unity version, what render pipeline, what packages are installed
   (check Packages/manifest.json and ProjectSettings/ProjectVersion.txt)
3. Any existing Editor tools or scripts (look in Assets/**/Editor/**/*.cs)
4. Any existing assembly definitions (.asmdef files)
5. The .csproj files to understand compilation setup
6. Any .gitignore or existing README files
7. Check if there are any test folders or test assembly definitions

Be thorough — list all .cs files, all .asmdef files, and key configuration files.
```

**Purpose:** Understand the full project structure, Unity version, dependencies, and existing patterns before designing anything.

**Key findings:**
- Unity 6 (6000.0.60f1), URP 17.0.4, C# 9.0, .NET 4.7.1
- Only 3 existing C# files (1 mock, 1 tutorial ScriptableObject, 1 tutorial editor)
- No assembly definitions — using default Assembly-CSharp
- Unity Test Framework 1.6.0 installed but no tests exist
- Existing editor tool pattern: `Assets/TutorialInfo/Scripts/Editor/ReadmeEditor.cs`

### Prompt 2: Architecture & Implementation Design

```
Design a detailed implementation plan for a Resilient SDK Proxy Tool in a Unity 6
(C# 9.0, .NET 4.7.1) project.

## Context
The project has a mock unstable SDK at Assets/Code/UnstableService.cs (namespace
Interview.Mocks, class UnstableLegacyService) that:
- Has a SendEvent(string eventName) method that returns bool
- 20% chance of Thread.Sleep(500) blocking the calling thread
- 30% chance of throwing Exception
- Uses UnityEngine.Random.value (which is main-thread only)
- Must NOT be modified

## Deliverables Required
1. ResilientAnalytics class — wraps UnstableLegacyService, thread-safe, Unity-friendly
2. Circuit Breaker — Closed/Open/HalfOpen states to protect from cascading failures
3. Unity Editor Tool — EditorWindow showing circuit state and total "saved" hitch time

## Design Constraints
- Must move SendEvent calls OFF the main thread
- Must be thread-safe
- UnityEngine.Random.value can only be called on the main thread, but the mock uses it
  internally — need to handle this
- Editor tool should use IMGUI (OnGUI) for the EditorWindow
- Circuit breaker should have configurable thresholds
- Need a way to dispatch results/callbacks back to the main thread

## Questions to Address
1. Threading strategy? (dedicated thread, Task.Run, thread pool)
2. Circuit breaker structure? (separate class or embedded)
3. Thread-safe metrics tracking?
4. ResilientAnalytics instantiation? (singleton MonoBehaviour, static, ScriptableObject)
5. Retry strategy?
6. Open-circuit event handling? (drop, queue, fire-and-forget)
7. File organization?

Please provide detailed class structures, key methods, and file layout.
Focus on production-quality patterns appropriate for a Staff Engineer level submission.
```

**Purpose:** Get a comprehensive architecture design with justified trade-offs for each decision point.

**Key outputs:**
- Dedicated background thread (not Task.Run) — justified by avoiding ThreadPool starvation
- Separate CircuitBreaker class — justified by SRP and testability
- MonoBehaviour singleton — justified by needing Update() and OnDestroy()
- Lock-free Interlocked operations — justified by single-writer thread model

### Prompt 3: User Decision Questions

Four questions were asked to align on design decisions:

1. **Open-circuit behavior:** "When the circuit breaker is Open and events are being dropped, should we silently discard them or queue them for retry when the circuit closes?"
2. **Circuit thresholds:** "What circuit breaker thresholds feel right for the 30% failure rate of this mock?"
3. **Callback API:** "Should the ResilientAnalytics provide optional callbacks to callers when an event succeeds or fails?"
4. **Unit tests:** "Do you want unit tests (using Unity Test Framework) included as part of the deliverables?"

### Prompt 4: Follow-up Question

After user chose "Queue for retry":

1. **Buffer limit:** "Since we're queuing events during Open state, should the retry buffer have a max capacity to prevent unbounded memory growth?"

---

## Appendix B — User Decisions Log

| # | Question | User's Choice | Impact on Design |
|---|----------|---------------|------------------|
| 1 | Open-circuit event handling | **Queue for retry** | Added bounded retry buffer (Queue<AnalyticsEvent>) to worker thread. Buffer flushes to main queue on Closed transition. |
| 2 | Circuit breaker thresholds | **5 failures / 10s cooldown** | Default constructor args: `failureThreshold: 5, openDurationMs: 10000` |
| 3 | Callback API | **Yes, optional callback** | `SendEvent(string, Action<bool>)` with main-thread dispatch via `_mainThreadActions` queue |
| 4 | Include unit tests | **Yes** | Added Phase 5 with EditMode tests for CircuitBreaker and AnalyticsMetrics |
| 5 | Retry buffer capacity | **Bounded (100 events)** | `_maxRetryBufferSize = 100`. Oldest events dropped when full, tracked via `_totalDropped` metric |

---

## Appendix C — Code Review Findings

The initial plan was reviewed with a focus on thread safety, Unity-specific pitfalls, correctness, and production readiness. Below is a summary of all issues found and the fixes applied.

### Critical Issues (Fixed)

| ID | Issue | Fix Applied |
|----|-------|-------------|
| C1 | `UnityEngine.Random.value` not safe from bg threads — plan handwaved it as "Unity 6 tolerates this" | Added "Known Risks & Mitigations" section with 4-tier fallback strategy. Updated Design Decision #9 with honest risk assessment. Added explicit verification step in Phase 6. |
| C2 | `ManualResetEventSlim.Wait()` + `Reset()` race condition — signal from `SendEvent()` can be lost between the two calls, adding up to 200ms latency | Replaced with `AutoResetEvent` which atomically resets on `WaitOne()`. Eliminates the race entirely. |

### High Priority Issues (Fixed)

| ID | Issue | Fix Applied |
|----|-------|-------------|
| H1 | `FlushRetryBuffer` called re-entrantly from `OnCircuitStateChanged` during `ProcessEventQueue` — fragile, risk of infinite loops if future logic added | Decoupled via `_flushRequested` flag. Callback sets flag; `ProcessEventQueue` flushes after its loop completes. Linear flow, no re-entrancy. |
| H2 | `_workAvailable.Dispose()` called after `Join(2000)` timeout — worker thread may still reference it → `ObjectDisposedException` | Only dispose after confirming thread exited. If `Join` times out, leak the handle intentionally (CLR finalizer cleans up). |
| H3 | `OnStateChanged?.Invoke()` can throw from subscriber, crashing worker thread | Wrapped in `RaiseStateChanged()` helper with try/catch. Exceptions logged, not propagated. |
| H4 | `EditorGUILayout.LongField` is editable — plan incorrectly said "not editable" | Wrapped in `EditorGUI.BeginDisabledGroup(true)` / `EndDisabledGroup()`. |

### Medium Priority Issues (Fixed)

| ID | Issue | Fix Applied |
|----|-------|-------------|
| M1 | No parameter validation in `CircuitBreaker` constructor | Added `ArgumentOutOfRangeException` guards for `failureThreshold < 1` and `openDurationMs < 0`. Added corresponding unit tests. |
| M2 | ProgressBar hardcodes `100` for max buffer size | Exposed `MaxRetryBufferSize` property on `ResilientAnalytics`. Editor reads it dynamically. |
| M3 | Tests use `Thread.Sleep(50)` for timing — flaky on CI | Injected `Func<long> getTimestamp` into `CircuitBreaker`. Tests use a controllable `FakeClock()`. Zero sleeps, deterministic, instant execution. |
| M4 | Callbacks for buffered events silently deferred — callers never notified until retry | Callback dispatched immediately with `false` when buffering. Event re-enqueued without callback to prevent double-invocation. |
| M5 | `SuccessRate` not atomic across two reads | Added inline comment documenting the limitation. Acceptable for display-only metric. |
| M6 | Missing `using System.Threading` in test file | Added `using System; using System.Collections.Generic;` to test imports. |

### Low Priority Issues (Fixed)

| ID | Issue | Fix Applied |
|----|-------|-------------|
| L1 | `readonly` field initializers allocate disposable objects for destroyed singleton duplicates | Moved all collection/handle initialization into `Awake()` after singleton check passes. |
| L2 | `TimeSpan.FromMilliseconds()` computed on every `AllowRequest()` call | Cached as `_openDurationTicks` in constructor. |

### Enhancements Added

| Feature | Rationale |
|---------|-----------|
| `AnalyticsMetrics.Reset()` | Allows resetting counters during development iteration without restarting play mode. Exposed as "Reset Metrics" button in editor window. |
| `ResilientAnalytics.IsReady` property | Returns `false` when circuit is Open. Feature teams can skip expensive payload construction when SDK is down. Displayed in editor window. |
| `CircuitBreaker(getTimestamp:)` parameter | Injectable time source enables deterministic testing. Production defaults to `DateTime.UtcNow.Ticks`. |
| 5 additional unit tests | `AllowRequest_BeforeTimeout_StaysOpen`, `OnStateChanged_DoesNotFireDuplicateForSameState`, `Constructor_ThrowsOnInvalidThreshold`, `Constructor_ThrowsOnNegativeDuration`, `FullCycle_Closed_Open_HalfOpen_Closed`, `Reset_ZerosAllCounters` |

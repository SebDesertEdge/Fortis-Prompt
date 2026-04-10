# Resilient Analytics — PlayerLoop Implementation

## Overview

The **PlayerLoop Analytics Service** wraps the deliberately unstable `UnstableLegacyService` mock SDK with a production-quality resilience layer for Unity 6. It protects the game from the SDK's two failure modes:

- **20% chance of 500ms thread-blocking hitches**
- **30% chance of throwing exceptions**

The service drains analytics events on the **main thread** (required because the legacy SDK calls `UnityEngine.Random.value` and `Debug.Log` internally) but bounds its per-frame impact using a configurable **frame budget**. Failed events are retried with **exponential backoff** on background threads. A **circuit breaker** prevents cascading failures by halting event dispatch when the SDK is consistently failing.

## Quick Start

```csharp
// From any thread — the call is lock-free
DiContainer.Resolve<IAnalyticsService>().SendEvent("level_complete");

// With completion callback (invoked on main thread)
DiContainer.Resolve<IAnalyticsService>().SendEvent("purchase", success =>
{
    Debug.Log($"Purchase event: {(success ? "sent" : "failed")}");
});
```

To enable this implementation:
1. Select the `AnalyticsConfig` asset at `Assets/Config/AnalyticsConfig.asset`
2. Set **Implementation** to **PlayerLoop**
3. Enter Play Mode

## Circuit Breaker

The circuit breaker has three states:

| State | Behaviour |
|---|---|
| **Closed** | All events are dispatched normally. Failures are counted. |
| **Open** | No events are dispatched. New events are buffered in a pending queue. Transitions to HalfOpen after `CircuitOpenDurationMs` elapses. |
| **HalfOpen** | One probe event is allowed through. If it succeeds → Closed (pending queue is flushed). If it fails → Open (timer resets). |

### Configuration

| Property | Default | Description |
|---|---|---|
| `FailureThreshold` | 5 | Consecutive failures before the circuit opens |
| `CircuitOpenDurationMs` | 10000 | How long (ms) the circuit stays Open before probing |

## Frame Budget

Each frame, the service processes queued events until either the queue is empty or the frame budget is exhausted. This prevents a large event backlog from monopolising the frame.

| Property | Default | Description |
|---|---|---|
| `FrameBudgetMs` | 8 ms | Maximum wall-clock time per frame spent on analytics dispatch |

Tune this based on your target frame rate:
- **60 FPS** (~16.6ms frame): 4–8ms budget is reasonable
- **30 FPS** (~33.3ms frame): 8–12ms budget is fine
- **Performance-critical scenes**: Lower to 2–4ms

The frame budget is editable at runtime via the **Fortis → Analytics Monitor** editor window.

## Retry Policy

Failed events are retried with exponential backoff on background threads:

| Property | Default | Description |
|---|---|---|
| `MaxRetryAttempts` | 4 | Maximum attempts before the event is dropped |
| `BaseDelayMs` | 500 | Base delay for exponential backoff |
| `MaxDelayMs` | 8000 | Ceiling for the backoff delay |
| `JitterFactor` | 0.1 | ±10% randomisation to avoid thundering herd |

**Formula:** `delay = Min(baseDelay × 2^attempt, maxDelay) ± jitter`

Uses `System.Random` (not `UnityEngine.Random`) since retry scheduling runs on ThreadPool threads.

## Editor Tools

### Analytics Monitor Window

**Menu: Fortis → Analytics Monitor**

Displays live circuit breaker state, failure rate, saved hitch time, event counts, pending queue, and provides test controls (send single/burst events, force reset). The frame budget is editable directly in this window.

### Inspector Component

Add the `AnalyticsMonitor` component to any GameObject in the scene. Its custom inspector draws a condensed read-only panel with the same metrics. Useful for quick checks without opening a separate window.

## Firebase Integration

The service reports exceptions to Firebase Crashlytics via the `IFirebaseReporter` interface.

To enable real Crashlytics reporting:

1. Import the Firebase Unity SDK
2. Add `FIREBASE_CRASHLYTICS` to **Player Settings → Scripting Define Symbols**
3. The service will automatically use `FirebaseCrashlyticsReporter` instead of the no-op fallback

Without the Firebase SDK, the project compiles cleanly using `NullFirebaseReporter`.

## Configuration Reference

All values are configured on the `AnalyticsConfig` ScriptableObject at `Assets/Config/AnalyticsConfig.asset`:

| Field | Default | Used By |
|---|---|---|
| `Implementation` | Baseline | All — selects which IAnalyticsService is bound |
| `FailureThreshold` | 5 | Circuit breaker consecutive failure limit |
| `CircuitOpenDurationMs` | 10000 | Circuit breaker open-state duration |
| `MaxRetryBufferSize` | 100 | Max events buffered while circuit is open |
| `FrameBudgetMs` | 8 | PlayerLoop — per-frame drain budget |
| `MaxRetryAttempts` | 4 | PlayerLoop — max exponential backoff retries |

## Architecture

```
Feature code (any thread)
        │  SendEvent("event_name")
        ▼
ConcurrentQueue<QueuedEvent>        ← lock-free enqueue
        │
        │  drained each frame in ITickable.Tick() (main thread)
        │  bounded by FrameBudgetMs
        ▼
[Main Thread] UnstableLegacyService.SendEvent()
        │
        ├── success → CircuitBreaker.RecordSuccess() + Metrics
        │
        └── failure → CircuitBreaker.RecordFailure() + Metrics
                │
                ├── RetryPolicy.ShouldRetry? → Task.Run(backoff + re-enqueue)
                │
                └── max retries exceeded → Metrics.RecordDrop()
```

**Circuit Open path:** events → pending queue (capped at MaxRetryBufferSize) → flushed back to intake when circuit closes.

## AI Tools Disclosure

Claude (Anthropic) was used to assist with architecture design, spec writing, and code generation. Core logic decisions — frame-budgeted main-thread drain, exponential backoff retry on ThreadPool, IAnalyticsService integration via existing DI container, and the decision to reuse the existing CircuitBreaker/AnalyticsMetrics for interface compatibility — were made by the candidate.

## What I'd Add With More Time

- **Sliding-window circuit breaker** — failure rate over last N attempts instead of consecutive count
- **Persistent event log** — write dropped events to disk for later replay
- **Per-event priority levels** — high-priority events bypass frame budget limits
- **Remote config** — circuit breaker thresholds fetched from server
- **Unit test suite** — NSubstitute mocks for CircuitBreaker, RetryPolicy, and full integration tests
- **PlayerLoop injection** — custom PlayerLoopSystem phase for more precise timing control than ITickable
- **Event batching** — coalesce multiple events into a single SDK call where supported

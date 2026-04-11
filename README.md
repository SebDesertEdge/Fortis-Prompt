# Resilient SDK Proxy Tool

## The Problem

Wrap an unstable analytics SDK (`UnstableLegacyService`) that blocks the main thread
for 500ms (20% of calls) and fails 30% of the time. Feature teams can't ship because
every analytics event risks a visible hitch.

## How I Approached This

I started by giving Claude Code the full problem description and the mock code. Claude
planned and implemented a solution using a dedicated background worker thread — the
textbook approach for offloading blocking I/O. But when I reviewed the implementation,
I caught something Claude missed: the mock calls `UnityEngine.Random.value` internally,
which is a main-thread-only API in Unity. Running the mock on a background thread throws
in the Editor and dev builds.

This creates an impossible constraint: `Thread.Sleep(500)` can only be avoided by moving
it off the main thread, but `UnityEngine.Random.value` forces you to stay on the main
thread. No alternative (Coroutines, Jobs, DOTS, Unity 6 Awaitable) solves both at once.

So I built my own implementation that stays on the main thread and uses a frame budget to
limit damage. Claude's worker-thread version stays in the repo as the "correct production
architecture" — because a real SDK would never use `UnityEngine.Random` internally.

## What's In The Repo

### My Implementation (active) — `Assets/Code/ResilientAnalytics.cs`
- Processes events on the main thread inside `Tick()`, bounded by a configurable
  frame budget (`FrameBudgetMs`)
- Each frame drains the event queue until the budget is spent
- A single `SendEvent()` can still hitch for 500ms if the mock triggers `Thread.Sleep`
  — the frame budget prevents *stacking* multiple hitches per frame, but can't interrupt
  one already in progress
- Retries use `Task.Run` with exponential backoff + jitter, re-enqueueing to the main
  queue after the delay
- Circuit breaker stops sending when failure threshold is hit

### Claude's Implementation (reference) — `Assets/Code/Claude-Implementation/`
- Dedicated worker thread with `AutoResetEvent` signaling
- Separate retry buffer with bounded capacity (100 events)
- Callbacks marshaled back to main thread via `ConcurrentQueue<Action>`
- Would fully eliminate main-thread hitches if the mock didn't use `UnityEngine.Random`
- Kept as a reference for what the production architecture should look like

### Shared Components
- **CircuitBreaker** — Thread-safe state machine (Closed/Open/HalfOpen) with
  `Interlocked` operations and injectable timestamp for deterministic testing
- **AnalyticsMetrics** — Thread-safe counters for enqueued, succeeded, failed,
  dropped, retried events plus saved hitch time
- **RetryPolicy** — Exponential backoff with jitter, `[ThreadStatic]` RNG
- **AnalyticsConfig** — ScriptableObject with circuit breaker thresholds, frame
  budget, and retry configuration

### Editor Tool — `Assets/Code/Editor/AnalyticsServiceWindow.cs`
- Edit mode: inspect and modify config values
- Play mode: live circuit breaker state (color-coded), real-time metrics
- Benchmark runner: fire 10-500 events, track wall clock time, throughput,
  success rate, saved hitch time, circuit opens
- Benchmark history across multiple runs

## Design Decisions

**Single queue vs. dual queue:** I used one `ConcurrentQueue` for everything — new events,
retries, and circuit-blocked deferrals. Simpler to reason about, fewer moving parts for
a take-home. The tradeoff is that when the circuit is open, dequeued events get re-enqueued
to the same queue, which can waste frame budget spinning. In production I'd separate
circuit-blocked events into a bounded pending buffer.

**Frame budget approach:** At 8ms default budget, the wrapper processes at most one event
per frame before the budget is exceeded by a 500ms hitch. This is intentional — it limits
the blast radius to one hitch per frame. The budget is configurable in the Inspector.

**Retry with exponential backoff:** Failed events are scheduled for retry on the ThreadPool
with exponential backoff (500ms base, 2x scaling, 8s cap, 10% jitter). The retry delay
runs on a background thread, but the re-enqueued event is processed on the main thread.

**Callbacks fire on eventual outcome:** The callback follows the event through retries and
fires once on final success or drop. I'm aware this means callers don't get immediate
failure notification — in production I'd fire `callback(false)` immediately and make
retries fire-and-forget.

## AI Usage

I used Claude Code throughout this project:
1. **Initial planning** — Gave Claude the problem description and mock code, discussed
   design decisions (retry buffer size, circuit breaker thresholds, callback API)
2. **First implementation** — Claude generated the worker-thread implementation
   (`Claude-Implementation/`)
3. **Code review** — Used Claude's code-review agent to analyze the plan and find bugs
   (identified race conditions, re-entrancy issues, disposal bugs)
4. **My implementation** — I wrote `Assets/Code/ResilientAnalytics.cs` myself after
   identifying the `UnityEngine.Random` threading constraint that Claude missed
5. **Self-review** — Used Claude to generate comparison docs analyzing both
   implementations (see `docs/`)
6. **Testing** — Unit tests written with Claude assistance

The key insight I brought that Claude missed: `UnityEngine.Random.value` makes the mock
fundamentally incompatible with background threading in dev builds. This drove the
architectural pivot to frame-budgeted main-thread processing.

## How to Run

1. Open in Unity 6 (2023.2+, URP 17)
2. Open `Assets/Scenes/BootScene.unity`
3. Enter Play Mode
4. Open **Tools > Analytics Monitor** for the live dashboard
5. Use the benchmark runner to fire events and observe circuit breaker behavior

## Known Issues & Future Improvements

- **Individual 500ms hitches still occur** — frame budget limits frequency, not duration.
  In production with a real SDK (no `UnityEngine.Random`), the worker-thread approach
  eliminates this entirely.
- **No CancellationToken on retry tasks** — `Task.Run` retries aren't cancelled on
  shutdown. Could produce orphaned tasks on scene transitions. Fix: add
  `CancellationTokenSource`, cancel in `Dispose()`.
- **Busy-loop when circuit opens mid-frame** — events dequeue and re-enqueue to the
  same queue, burning frame budget. Fix: `break` instead of `continue` when
  `AllowRequest()` returns false inside the processing loop.
- **Unbounded queue growth** — no cap on the main queue when circuit is open. Production
  fix: separate bounded pending buffer with drop tracking (like Claude's implementation).
- **No integration tests** — unit tests cover CircuitBreaker, Metrics, and RetryPolicy,
  but not the ResilientAnalytics wrapper itself. Would need an `ILegacyService` interface
  or factory to inject a deterministic fake.
- **PlayerLoop injection** — could shift SDK processing to after `PostLateUpdate` so
  hitches occur after the frame is presented, reducing perceptual impact.

## Tests

49 unit tests in `Assets/Tests/EditMode/`:
- `CircuitBreakerTests` (22) — state transitions, thresholds, timing, events, exception safety
- `AnalyticsMetricsTests` (16) — counter increments, success rate, reset
- `RetryPolicyTests` (11) — exponential scaling, jitter, bounds, edge cases

Run via Unity Test Runner (Window > General > Test Runner > EditMode > Run All).

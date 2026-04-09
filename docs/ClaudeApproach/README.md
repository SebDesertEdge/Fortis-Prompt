In order to analyze the code challenge I give all the information to Claude Code in order to understand everything and chat 
the claude about the implementati that we want to do. I will describe here the steps that I follow and the promps that I give to Claude.

1. Ask Claude to understand the code challenge. And plan a first implemenation of it.

```
Some third-party Unity SDKs (Analytics, Ads, Backend) have a common flaw: they are leaky
and often crash or hang the main thread if the network or local state is unstable.
The Task: Build a Resilient SDK Proxy Tool that wraps a mock unstable API. The goal is to
provide a rock-solid, thread-safe, and "Unity-friendly" interface for feature teams to use.
Additional information
● You will be provided with an unstable service mock written in C#.
● You must not modify the mock, but you should wrap the behaviour.

Objective: Build a wrapper for an events SDK that is notoriously unstable. It frequently
  hangs the main thread for 500ms and has a 20% failure rate. Feature teams are
  complaining that the game hitches whenever an event is sent.. Check the file @Assets/Code/UnstableService.cs to analyze everything.

Deliverables:
1. A ResilientAnalytics class that wraps the provided UnstableService.
2. Implementation of a Circuit Breaker to protect the game from constant failures.
3. A small Unity Editor Tool that shows the current status of the SDK (Closed, Open, Half-Open) and the total "saved" hitch time.

Ask me as many question needed so we can solve this problem toguether
```


Questions asked from Claude:

```
1. Open state behavior: "When the circuit breaker is Open and events are being dropped, should we silently discard them or queue them for retry when the circuit closes?"
    - You chose: Queue for retry
2. Thresholds: "What circuit breaker thresholds feel right for the 30% failure rate of this mock?"
   - You chose: 5 failures / 10s cooldown
3. Callbacks: "Should the ResilientAnalytics provide optional callbacks to callers when an event succeeds or fails?"
   - You chose: Yes, optional callback
4. Tests: "Do you want unit tests (using Unity Test Framework) included as part of the deliverables?"
   - You chose: Yes, include tests
5. Buffer limit: "Since we're queuing events during Open state, should the retry buffer have a max capacity to prevent unbounded memory growth?"
   - You chose: Bounded (100 events)
```

2. Ask Claude to write the plan to `docs/ClaudeApproach/claude-implementation-plan.md`

3. Used this promp to review the plan from a code reviewr expert perspective.
```
Use a code-review agent to fully analyze the plan and propose changes to it
```

4. The code review agent came back with a list of changes to the plan.
```
Critical Fixes

  - UnityEngine.Random risk — Added a full "Known Risks & Mitigations" section with a 4-tier fallback strategy instead of handwaving it
  - Signal-loss race condition — Replaced ManualResetEventSlim with AutoResetEvent (atomic wait+reset)

  High Priority Fixes

  - Re-entrant flush — Decoupled FlushRetryBuffer from the state change callback via a _flushRequested flag
  - Dispose-after-timeout crash — AutoResetEvent only disposed after confirming thread exited
  - Subscriber exceptions — OnStateChanged wrapped in RaiseStateChanged() with try/catch
  - Editable LongField — Wrapped in BeginDisabledGroup(true)

  Medium Priority Fixes

  - Constructor parameter validation, dynamic progress bar max, injectable time abstraction for tests (zero Thread.Sleep), immediate callback dispatch on buffer, atomicity comment on
  SuccessRate, missing imports

  Enhancements Added

  - AnalyticsMetrics.Reset() + editor button
  - ResilientAnalytics.IsReady property
  - 6 additional unit tests (including full lifecycle test, validation tests, duplicate-fire test)
  - Appendix C documenting all review findings and fixes applied
```

5. Ask Claude to use a coder agent to implement the plan.
```
   Use a coder agent to implement the plan on @docs/ClaudeApproach/claude-implementation-plan.md, all the code should be writen to @Assets/Code/Claude-Implementation
```
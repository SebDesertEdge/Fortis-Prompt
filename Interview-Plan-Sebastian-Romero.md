# Staff Client Engineer Interview Plan — Sebastian Romero

## Context

Sebastian completed the Resilient SDK Proxy Tool take-home for the Fortis Games Staff Client Engineer role. He built a thread-safe analytics wrapper around `UnstableLegacyService` (a mock SDK that blocks 500ms 20% of the time and fails 30% of the time), a circuit breaker, retry with exponential backoff, a custom DI framework, and a full editor monitoring/benchmarking tool. He was transparent about using Claude Code and caught a critical threading bug that Claude missed (`UnityEngine.Random.value` is main-thread-only). His profile is strong on backend/distributed systems (12+ years, Node.js/AWS at scale) but lighter on Unity client depth (last direct Unity work was 2012-2015 at DeNA West).

**This document is the complete interviewer guide for two 60-minute panels.**

---

## PANEL 1: ASSESSMENT DEEP-DIVE (60 minutes)

### Interviewer Prep

Have these files open:
- `Assets/Code/ResilientAnalytics.cs` — the active implementation
- `Assets/Code/Claude-Implementation/ResilientAnalytics.cs` — the alternative worker-thread version (not wired up)
- `Assets/Code/Claude-Implementation/CircuitBreaker.cs`
- `Assets/Code/Editor/AnalyticsServiceWindow.cs`
- `Assets/Code/UnstableService.cs` — the mock (read-only constraint)
- `Assets/Code/Core/DependencyInjection/DiContainer.cs`

---

### Section 1: Architecture Walkthrough (12 min)

**Q1.1:** "Walk me through the architecture of your solution at a high level. What are the major components and how do they interact?"

| Strong (3-4) | Red Flag (1) |
|---|---|
| Clearly describes the pipeline: `SendEvent` enqueues to `ConcurrentQueue`, `LateTick()` drains on main thread under frame budget, failed events retry via `Task.Run` with exponential backoff, circuit breaker gates calls. Mentions that SDK calls must stay on main thread because `UnityEngine.Random.value` is main-thread-only. | Cannot explain the threading model. Confuses which operations happen on which thread. Does not mention the `UnityEngine.Random` constraint unprompted. |

**Q1.2:** "You have two implementations — your POCO-based version and the Claude-generated MonoBehaviour version. Why is yours the active one?"

| Strong (3-4) | Red Flag (1) |
|---|---|
| Caught the `UnityEngine.Random.value` threading constraint that Claude missed. The Claude version would crash in dev builds. Kept it as reference because the worker-thread architecture is what you'd use with a real SDK. Shows awareness of mock constraints vs. real-world constraints. | Cannot articulate why the Claude version fails. "I just liked mine better" with no technical grounding. |

**Q1.3:** "Why a POCO class with DI lifecycle interfaces rather than a MonoBehaviour?"

| Strong (3-4) | Red Flag (1) |
|---|---|
| POCOs are more testable, don't require a GameObject, easier to manage in DI. Lifecycle interfaces (`IInitializable`, `ITickable`, `ILateTickable`, `IDisposable`) give the same hooks without MonoBehaviour overhead. Analytics has no reason to be a Unity component. | Cannot explain tradeoffs between MonoBehaviour and POCO. Only chose it because DI framework was there. |

---

### Section 2: Threading & Concurrency (15 min)

This is the most technically dense area and the strongest signal for Staff-level evaluation.

**Q2.1:** "In `LateTick()`, you dequeue an event, check circuit breaker, and if blocked, re-enqueue and break. What happens if the circuit stays open for an extended period while events keep arriving?"

| Strong (3-4) | Red Flag (1) |
|---|---|
| Acknowledges this is a weakness. Each frame dequeues one event, finds circuit open, re-enqueues, breaks. Meanwhile new events accumulate. Queue grows without bound. In production: bounded buffer, separate queue for circuit-blocked events, or drop policy after threshold. | Claims the `break` prevents any problem. Does not recognize unbounded queue growth. |

**Q2.2:** "Frame budget is 8ms, but a single SDK call can block 500ms. How does the frame budget actually help, and what does it fail to prevent?"

| Strong (3-4) | Red Flag (1) |
|---|---|
| Budget limits how many events are attempted per frame, not duration of any single call. After one 500ms hitch, the stopwatch is past budget so no more events dequeue that frame. Prevents *stacking* hitches (3 events = 1500ms), but cannot prevent the individual 500ms hitch. Inherent limitation of main-thread processing with a synchronous SDK. | Claims the frame budget prevents hitches entirely. Does not understand the check happens *between* events, not *during*. |

**Q2.3:** "Walk me through what thread each operation in `ScheduleRetry` runs on and why that matters."

| Strong (3-4) | Red Flag (1) |
|---|---|
| `ScheduleRetry` called on main thread (from `LateTick`). `Task.Run` schedules lambda on ThreadPool. `Task.Delay` runs async on ThreadPool. After delay, `_pendingQueue.Enqueue` runs on ThreadPool — safe because `ConcurrentQueue` is thread-safe. Re-enqueued event processed on main thread in next `LateTick`. Explains why `ConcurrentQueue` is necessary (multiple threads writing). | Cannot trace thread ownership across the async boundary. Does not know what `Task.Run` does with respect to threading. |

**Q2.4:** "If I call `Dispose()` while retry tasks are still in-flight on the ThreadPool, what happens?"

| Strong (3-4) | Red Flag (1) |
|---|---|
| `CleanUp()` cancels the CTS, causing pending `Task.Delay` to throw `TaskCanceledException` (caught and swallowed). Race condition: a task could be between `Task.Delay` completing and `Enqueue` — event gets enqueued to a queue nobody will drain. Acceptable for shutdown but improvable by checking token before enqueuing. | Does not recognize any race condition. "Cancellation handles everything." |

**Q2.5:** "CircuitBreaker uses `Interlocked` instead of locks. Why, and are there correctness issues?"

| Strong (3-4) | Red Flag (1) |
|---|---|
| Lock-free, avoids overhead and potential deadlocks. `TryTransition` uses `Interlocked.CompareExchange` for atomic state swap. Potential TOCTOU in `RecordFailure`: failure count incremented, then State read separately — another thread could trip the circuit between. But `Trip()` uses `Interlocked.Exchange` which is idempotent for Open state, so double-tripping is harmless. | Cannot explain what `Interlocked.CompareExchange` does. Says locks would be equivalent with no tradeoff awareness. |

---

### Section 3: Production Readiness (12 min)

**Q3.1:** "If this shipped tomorrow in a mobile game, what are your top three changes?"

Strong answer mentions at least 3 of:
1. **Bounded retry buffer** — queue grows without limit when circuit is open
2. **Event persistence** — app crash/kill loses all queued events; needs disk persistence (SQLite, flat file) with replay on next launch
3. **Event prioritization** — revenue events should never be dropped; cosmetic events can be
4. **Immediate failure callback** — currently callbacks wait until final retry exhaustion
5. **Observability** — structured logging, metrics export, circuit open alerts
6. **Off-main-thread SDK calls** — once the real SDK doesn't use `UnityEngine.Random`

Red flag: "It's production-ready as-is" or can only name one trivial change.

**Q3.2:** "The benchmark can send events from `Task.Run`. But `SendEvent` just enqueues — the actual SDK call happens on main thread in `LateTick`. Is there any issue?"

| Strong (3-4) | Red Flag (1) |
|---|---|
| Sending from `Task.Run` is fine: `SendEvent` only does `Interlocked.Increment` (atomic) and `ConcurrentQueue.Enqueue` (thread-safe). The concern is the benchmark might mislead users into thinking events are *processed* on background threads. Throughput numbers reflect main-thread drain rate, not enqueue rate. | Does not recognize `SendEvent` is thread-safe, or incorrectly claims it would cause issues. |

**Q3.3:** "You have tests for CircuitBreaker, Metrics, and RetryPolicy, but not the wrapper itself. Why, and how would you approach it?"

| Strong (3-4) | Red Flag (1) |
|---|---|
| Wrapper is hard to test because: (1) `new UnstableLegacyService()` inside `Initialize()` can't be injected, (2) depends on Unity lifecycle (`LateTick` being called). Fix: extract service behind an interface, inject via DI, use fake in tests. `LateTick` is already callable from test code. Acknowledges deliberate scope cut. | Does not recognize `new UnstableLegacyService()` as a testability problem. No strategy for testing async/threaded code. |

---

### Section 4: DI Framework & Design (8 min)

**Q4.1:** "You built a custom DI instead of Zenject or VContainer. What are the tradeoffs for a production game?"

| Strong (3-4) | Red Flag (1) |
|---|---|
| Custom DI lacks scoped lifetimes, factory bindings, decorators, compile-time validation, and editor tooling that established frameworks provide. Reflection-based `[Inject]` has startup performance implications (though O(1) after binding). Advantage: full control, no third-party risk. For a real game, an established framework is usually the right call. | Cannot articulate what Zenject/VContainer provide that this doesn't. Presents custom DI as categorically superior. |

**Q4.2:** "In `DiContainer`, `_objectFields` and `_injection` are instance fields rather than locals in `ResolveDependenciesInternal`. What's the consequence?"

| Strong (3-4) | Red Flag (1) |
|---|---|
| Shared state on the instance. If called concurrently (unlikely but possible), it's a race condition. Even without concurrency, retains references unnecessarily. Should be local variables. Likely a micro-optimization attempt that isn't meaningful for a one-time startup operation. | Does not notice the issue or thinks instance fields are equivalent to locals. |

---

### Section 5: AI Collaboration (8 min)

**Q5.1:** "How do you decide when to accept generated code vs. rewrite it yourself?"

| Strong (3-4) | Red Flag (1) |
|---|---|
| Clear methodology: AI is good for boilerplate, initial architecture, test generation. Must review every line against domain-specific constraints. Caught `UnityEngine.Random` because he understood Unity's threading model. Describes systematic review process. Staff-level: AI accelerates velocity but engineer remains accountable for correctness. | "I just ran it and it worked." No review process. Cannot distinguish between code he understands and code he doesn't. |

**Q5.2:** "If a junior engineer submitted the Claude-generated worker-thread implementation in a code review, how would you give feedback?"

| Strong (3-4) | Red Flag (1) |
|---|---|
| Praises the architecture (correct pattern for offloading blocking I/O), then identifies the specific issue (`UnityEngine.Random` on background thread). Uses it as a teaching moment. Suggests keeping the architecture but fixing the constraint. Constructive, not just rejecting. | Simply rejects it. Cannot articulate constructive feedback. |

---

### Section 6: Wrap-Up (5 min)

- "Anything about your implementation you want to highlight that we haven't discussed?"
- "What questions do you have about the role or team?"

---

### Panel 1 Scoring Rubric

| Dimension | Weight | Pass (3) | Strong (4) |
|---|---|---|---|
| **Threading & Concurrency** | 30% | Correctly traces all thread boundaries. Identifies frame budget limitation. Knows the disposal race. | Plus: identifies TOCTOU in CircuitBreaker, proposes bounded buffer improvements, discusses memory model implications. |
| **Architectural Reasoning** | 25% | Explains all decisions with tradeoffs. Knows what production changes are needed. | Proposes specific production architecture (PlayerLoop injection, event persistence, priority queues). |
| **Production Readiness** | 20% | Identifies 3+ issues with concrete solutions. Understands testability gaps. | Prioritized roadmap with effort estimates. Discusses monitoring, alerting, graceful degradation. |
| **Communication** | 15% | Concise, structured explanations. Proactively addresses tradeoffs. | Teaches as they explain. Anticipates follow-ups. Uses diagrams unprompted. |
| **AI Collaboration Maturity** | 10% | Systematic review process. Caught the critical threading bug. | Articulates a general framework for AI-assisted development. Can teach others. |

**Pass threshold:** Average >= 3.0, no dimension below 2.0. Threading & Concurrency must be >= 3.0 for Staff-level pass.

---

## PANEL 2: TECHNICAL WHITEBOARD (60 minutes)

### Section 1: Unity Client Architecture (15 min)

**Q1.1:** "You're building an SDK that multiple game teams at Fortis will integrate. It needs to initialize before any game code runs, survive scene loads, and be configurable per-game. How would you architect the SDK lifecycle in Unity?"

| Strong (3-4) | Red Flag (1) |
|---|---|
| `RuntimeInitializeOnLoadMethod` for early init, `DontDestroyOnLoad` for persistence, ScriptableObject-based config, `[DefaultExecutionOrder]` for ordering. Discusses singleton vs testability tension. May mention PlayerLoop API for custom update phases. Staff-level: SDK should be non-intrusive (no scene dependencies, no required MonoBehaviours, purely code-driven init). | Only knows `DontDestroyOnLoad` and singletons. No awareness of initialization ordering challenges. |

**Q1.2:** "Explain Unity's main thread constraint. What can and can't run off the main thread, and what are the common patterns?"

| Strong (3-4) | Red Flag (1) |
|---|---|
| Unity API calls (Transform, GameObject, Rendering, Physics, `UnityEngine.Random`, most of `UnityEngine.*`) must be on main thread. Off-thread safe: pure C#, `System.*`, network/file I/O. Patterns: `ConcurrentQueue<Action>` for callback marshaling, `SynchronizationContext`, Unity 6 `Awaitable`, UniTask, Job System for parallel data processing. Unity 6 Awaitables auto-resume on main thread. | Cannot name main-thread-only APIs beyond the example already encountered. Doesn't know Job System or async patterns. |

**Q1.3:** "Difference between Update, LateUpdate, FixedUpdate? When would you use each for SDK-level work?"

| Strong (3-4) | Red Flag (1) |
|---|---|
| `Update`: once per frame, variable timestep. `LateUpdate`: after all Updates (camera follow, post-processing). `FixedUpdate`: fixed timestep (physics). For SDK: analytics processing in `LateUpdate` or after `PostLateUpdate` via PlayerLoop injection, so hitches occur after gameplay logic. | Cannot explain the difference. Doesn't know about PlayerLoop. |

---

### Section 2: System Design Exercise (20 min)

**Prompt:** "Design a real-time event system for a mobile game that handles: in-game currency transactions, player progression events, crash/error reporting, and A/B test assignment logging. Must work offline (airplane mode), handle app kills gracefully, and prioritize data by importance. Sketch the architecture on the whiteboard."

**Strong answer includes:**
- Event classification by priority tier (critical: currency/purchases, important: progression, best-effort: telemetry)
- Persistent local storage (SQLite or binary file) with WAL for crash safety
- Batching with configurable flush intervals per priority tier
- Offline queue with bounded size and eviction policy (evict lowest priority first)
- Network layer with retry, circuit breaker, backpressure
- Schema versioning for events (forward/backward compatibility)
- Idempotency keys for currency events to prevent double-counting
- Battery and data-usage awareness (batch on cellular, flush on WiFi)
- Delivery guarantee tradeoffs (at-least-once vs. at-most-once vs. exactly-once)

**Staff differentiator:** Discusses SDK integration contract — API surface, threading guarantees, initialization requirements, graceful degradation (never crash the game). Discusses SDK versioning and backward compatibility with older game builds.

**Red flag:** Flat architecture with no prioritization. No persistence. No offline consideration. Only happy path.

---

### Section 3: Mobile Performance & Memory (10 min)

**Q3.1:** "QA reports frame drops every 30 seconds on low-end Android. Walk me through your debugging process."

| Strong (3-4) | Red Flag (1) |
|---|---|
| Unity Profiler (CPU, GPU, Memory modules). Check GC allocation spikes at 30s intervals. Timeline view for system spikes. Check asset loading. Android: `adb` + systrace or Android GPU Inspector. Consider thermal throttling (consistent 30s interval is suspicious). Check `Application.targetFrameRate`. Memory: texture streaming, asset bundle unloading, managed heap fragmentation. | "Use the Profiler." No systematic approach. Doesn't consider thermal throttling or platform-specific tools. |

**Q3.2:** "Most common GC allocation sources in Unity C# and how to mitigate them in a hot path?"

| Strong (3-4) | Red Flag (1) |
|---|---|
| Boxing value types, string concatenation (use `StringBuilder`), LINQ allocations (closures, iterators), delegate/lambda allocations, `foreach` on non-generic collections, `GetComponent`, `Debug.Log` in release (string formatting still allocates). Mitigations: object pooling, pre-allocated arrays, `NativeArray`/`NativeList`, `stackalloc`, avoiding closures by passing state via parameters, struct enumerators. Profiler's GC Alloc column for identification. | Cannot name more than 1-2 sources. Doesn't know about struct enumerators or LINQ allocation issues. |

---

### Section 4: Technical Leadership (10 min)

**Q4.1:** "You join Fortis. The client team uses a mix of singletons, static managers, and occasional DI. Code reviews are inconsistent, no shared architecture guide. How do you improve the codebase without alienating the team?"

| Strong (3-4) | Red Flag (1) |
|---|---|
| Understand why current patterns exist (ship pressure, team history). Build relationships first. Identify highest-pain-point pattern. Write an ADR/RFC with problem, options, recommendation. Implement new pattern in one system as reference. Pair with team members. Opt-in initially, gradually migrate. Don't rewrite working code without business reason. Set up linting/analyzer rules for new code. | "Rewrite everything to use proper DI." Top-down mandate without buy-in. No empathy for team context. |

**Q4.2:** "A designer wants to add a feature you believe will cause significant tech debt. How do you handle it?"

| Strong (3-4) | Red Flag (1) |
|---|---|
| Understand business value and timeline pressure. Quantify tech cost: what does debt look like, what future work does it block, maintenance burden. Present options: quick way (with documented debt + follow-up ticket), clean way (with timeline impact), middle ground. Make tradeoff visible to stakeholders. Staff engineer's job is to make tradeoffs visible, not block product decisions. | Always says yes (people-pleaser) or always says no (gatekeeper). Cannot quantify or communicate tradeoffs to non-technical stakeholders. |

---

### Section 5: Background Deep-Dive (5 min)

**Q5.1:** "Your background is primarily backend. What draws you to a Unity client role, and how do you plan to ramp up on client-side areas where you have less depth?"

| Strong (3-4) | Red Flag (1) |
|---|---|
| Genuine interest in client-side game development (not just "any Staff role"). Concrete learning plan (specific Unity areas, resources, projects). Leverages backend strength as additive (distributed systems, resilience, observability). Acknowledges gap honestly without being defeatist. | Dismisses client-side complexity. "Unity is just C#, I'll pick it up." No concrete ramp-up plan. |

---

### Panel 2 Scoring Rubric

| Dimension | Weight | Pass (3) | Strong (4) |
|---|---|---|---|
| **Unity Client Knowledge** | 25% | Solid understanding of lifecycle, threading, initialization. Knows multiple patterns per problem. | Deep knowledge of PlayerLoop injection, Unity 6 Awaitables, ECS/DOTS, platform-specific debugging. |
| **System Design** | 30% | Comprehensive design covering all major concerns. Clear component boundaries. | Plus: SDK API contract, versioning, backward compatibility, integration testing strategy. |
| **Performance & Debugging** | 20% | Systematic debugging approach. Platform-specific tools. 5+ GC allocation sources with mitigations. | Thermal throttling, IL2CPP implications, Burst compilation, memory fragmentation, draw call batching. |
| **Technical Leadership** | 25% | Empathy, incremental approach, stakeholder communication. Concrete strategies. | Specific past examples. Discusses organizational dynamics, not just technical solutions. |

**Pass threshold:** Average >= 3.0, no dimension below 2.0. System Design must be >= 3.0. Unity Client Knowledge at 2.0 acceptable *only* if System Design and Technical Leadership are both >= 3.5 (indicating strong fundamentals and fast ramp-up capacity).

---

## OVERALL HIRING DECISION FRAMEWORK

### Hire (Staff Level)
- Panel 1 average >= 3.0, Panel 2 average >= 3.0
- No dimension below 2.0 across either panel
- Threading & Concurrency (P1) >= 3.0
- System Design (P2) >= 3.0
- Demonstrates Staff-level behaviors: owns tradeoffs, teaches as they explain, thinks about team impact, anticipates failure modes

### Conditional Hire (with Ramp-Up Plan)
- Panel 1 average >= 3.0, Panel 2 average >= 2.5
- Unity Client Knowledge at 2.0 but compensated by strong System Design (>= 3.5) and Technical Leadership (>= 3.5)
- Concrete plan to close Unity knowledge gaps within 3-6 months
- Hiring manager accepts the ramp-up risk

### No Hire
- Any dimension at 1.0
- Panel 1 average below 2.5 (cannot explain own take-home code)
- Threading & Concurrency below 2.5
- System Design below 2.5
- Shows Senior-level thinking only (solves the immediate problem) without Staff-level thinking (team impact, production operations, organizational dynamics)

---

## CALIBRATION NOTES FOR INTERVIEWERS

**On Sebastian's profile:** His backend experience means he'll likely be strong on threading, system design, and resilience patterns. Credit that strength fully. Where he may be weaker is Unity-specific client knowledge (PlayerLoop, rendering pipeline, asset management, mobile optimization). The key judgment: does he have the learning velocity and systems thinking to ramp up on Unity client specifics within 3-6 months, and is his backend/systems strength additive enough to justify the ramp-up period?

**Fair probing of Unity gaps:** Don't ask trivia about Unity APIs. Ask questions where strong systems thinking can partially compensate for lighter Unity experience. Look for "I don't know that specific API, but here's how I'd approach finding out" versus silence or false confidence.

**AI usage transparency:** Sebastian was upfront about using Claude Code. This is a positive signal for intellectual honesty. The key evaluation: can he distinguish between generated code he understands and code he doesn't? He caught the `UnityEngine.Random` threading issue that Claude missed -- strong data point.

**Key files for reference during interviews:**
- `Assets/Code/ResilientAnalytics.cs` -- main implementation
- `Assets/Code/Claude-Implementation/CircuitBreaker.cs` -- circuit breaker
- `Assets/Code/Editor/AnalyticsServiceWindow.cs` -- editor tool
- `Assets/Code/UnstableService.cs` -- the mock SDK
- `Assets/Code/Core/DependencyInjection/DiContainer.cs` -- DI framework

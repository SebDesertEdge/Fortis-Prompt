# File Manifest — PlayerLoop Analytics Service

## Created Files

| # | Path | Description |
|---|---|---|
| 1 | `Assets/Analytics/Runtime/IFirebaseReporter.cs` | `IFirebaseReporter` interface — single method `ReportException(Exception, string)` |
| 2 | `Assets/Analytics/Runtime/FirebaseCrashlyticsReporter.cs` | `FirebaseCrashlyticsReporter` (behind `#if FIREBASE_CRASHLYTICS`) + `NullFirebaseReporter` (always compiled) |
| 3 | `Assets/Analytics/Runtime/RetryPolicy.cs` | `RetryConfig` struct + `RetryPolicy` static class — `ShouldRetry()`, `GetDelay()` with exponential backoff + jitter |
| 4 | `Assets/Analytics/Runtime/PlayerLoopAnalyticsService.cs` | Main implementation: `IAnalyticsService`, `IInitializable`, `ITickable`, `IDisposable` — frame-budgeted drain, background retry, pending queue |
| 5 | `Assets/Analytics/Runtime/AnalyticsMonitor.cs` | Empty `MonoBehaviour` — scene anchor for custom inspector |
| 6 | `Assets/Analytics/Editor/ResilientAnalyticsEditorWindow.cs` | Editor window: Fortis → Analytics Monitor — live metrics, test controls, frame budget editing |
| 7 | `Assets/Analytics/Editor/ResilientAnalyticsInspector.cs` | Custom inspector for `AnalyticsMonitor` — condensed read-only metrics panel |
| 8 | `Assets/Analytics/README.md` | Module documentation — overview, quick start, configuration reference |

## Modified Files

| # | Path | Changes |
|---|---|---|
| 1 | `Assets/Code/AnalyticsConfig.cs` | Added `PlayerLoop` to `AnalyticsImplementation` enum (position 5). Added `FrameBudgetMs` (float, default 8) and `MaxRetryAttempts` (int, default 4) fields with headers. |
| 2 | `Assets/Code/MainGameInstaller.cs` | Added `using Fortis.Analytics.PlayerLoop`. Added `case AnalyticsImplementation.PlayerLoop: Container.Bind<PlayerLoopAnalyticsService>(addInterfaces: true)`. |

## Documentation Files

| # | Path | Description |
|---|---|---|
| 1 | `docs/claude-ios/implementation-plan.md` | Full implementation plan, spec deviations, architecture decisions, thread safety analysis |
| 2 | `docs/claude-ios/design-considerations.md` | Trade-off analysis, rationale for each major design choice |
| 3 | `docs/claude-ios/file-manifest.md` | This file — inventory of all created/modified files |

## Namespace Map

| Namespace | Files |
|---|---|
| `Fortis.Analytics.PlayerLoop` | `PlayerLoopAnalyticsService`, `IFirebaseReporter`, `FirebaseCrashlyticsReporter`, `NullFirebaseReporter`, `RetryPolicy`, `RetryConfig`, `AnalyticsMonitor` |
| `Fortis.Analytics.PlayerLoop.Editor` | `ResilientAnalyticsEditorWindow`, `ResilientAnalyticsInspector` |
| `Fortis` (modified) | `AnalyticsConfig` (added enum value + fields), `MainGameInstaller` (added binding case) |

# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a Unity 6 (URP 17) project for a Fortis Games Staff Client Engineer take-home prompt. The task is to build a **Resilient SDK Proxy Tool** that wraps the provided `UnstableLegacyService` mock — a deliberately buggy third-party analytics SDK that blocks the main thread (~500ms hitches) and fails ~30% of the time.

## Key Constraint

**Do not modify `Assets/Code/UnstableService.cs`** — it is the provided mock and must remain untouched. All work wraps its behavior.

## Deliverables

1. **`ResilientAnalytics`** — a thread-safe, Unity-friendly wrapper around `UnstableLegacyService` that moves calls off the main thread
2. **Circuit Breaker** — protects the game from cascading failures (states: Closed, Open, Half-Open)
3. **Unity Editor Tool** — editor window showing circuit breaker state, saved hitch time, and SDK health metrics

## Architecture

- `Assets/Code/UnstableService.cs` — the mock SDK (namespace `Interview.Mocks`). Simulates 500ms Thread.Sleep hitches (20% chance) and random exceptions (30% failure rate). **Read-only.**
- `Assets/Code/IAnalyticsService.cs` — interface exposing `CircuitBreaker`, `AnalyticsMetrics`, `IsReady`, `MaxRetryBufferSize`, and `SendEvent`
- `Assets/Code/AnalyticsService.cs` — baseline analytics service implementation
- `Assets/Code/AnalyticsConfig.cs` — `ScriptableObject` config with `UseClaudeImplementation` toggle, circuit breaker thresholds (`FailureThreshold`, `CircuitOpenDurationMs`), and retry buffer size
- `Assets/Code/MainGameInstaller.cs` — DI installer (`MonoInstaller`). Binds either `ResilientAnalytics` or `AnalyticsService` based on the `UseClaudeImplementation` flag
- `Assets/Code/Claude-Implementation/ResilientAnalytics.cs` — the resilient wrapper (deliverable 1)
- `Assets/Code/Claude-Implementation/CircuitBreaker.cs` — circuit breaker implementation (deliverable 2)
- `Assets/Code/Claude-Implementation/AnalyticsMetrics.cs` — SDK health metrics tracking
- `Assets/Code/Claude-Implementation/Editor/ResilientAnalyticsWindow.cs` — editor window for monitoring (deliverable 3)
- `Assets/Code/Editor/AnalyticsServiceWindow.cs` — baseline analytics editor window
- `Assets/Code/Core/DependencyInjection/` — custom lightweight DI framework (`DiContainer`, `MonoInstaller`, `InjectAttribute`, lifecycle interfaces: `IInitializable`, `ITickable`, `IFixedTickable`, `ILateTickable`, `ICleanable`, `IPausable`, `IFocusable`, `IDisposable`)
- `Assets/Code/Core/Utilities/` — `ScriptableObjectUtility` helper
- `Assets/Config/AnalyticsConfig.asset` — the ScriptableObject config asset instance
- Render pipeline: URP with separate Mobile and PC renderer/asset configs in `Assets/Settings/`
- Boot scene: `Assets/Scenes/BootScene.unity` (replaces `SampleScene`)

## Development

- **Unity version**: Unity 6 (URP 17.0.4)
- **IDE**: Visual Studio or Rider (solution file: `Fortis-Prompt.sln`)
- **Tests**: Tests were previously under `Assets/Tests/` but have been removed. Unity Test Framework (`com.unity.test-framework` 1.6.0) is still available — recreate tests under `Assets/Tests/` with appropriate assembly definitions if needed
- **Editor tools**: Place custom editor scripts in an `Editor/` folder so they are excluded from builds
- **DI**: The project uses a custom lightweight dependency injection framework (not Zenject/VContainer) in `Assets/Code/Core/DependencyInjection/`. Bindings are configured in `MainGameInstaller`

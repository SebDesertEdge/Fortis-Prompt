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
- All new code should go under `Assets/Code/`
- Render pipeline: URP with separate Mobile and PC renderer/asset configs in `Assets/Settings/`
- Single scene: `Assets/Scenes/SampleScene.unity`

## Development

- **Unity version**: Unity 6 (URP 17.0.4)
- **IDE**: Visual Studio or Rider (solution file: `Fortis-Prompt.sln`)
- **Tests**: Unity Test Framework (`com.unity.test-framework` 1.6.0) is included — place tests under `Assets/Tests/` with appropriate assembly definitions
- **Editor tools**: Place custom editor scripts in an `Editor/` folder so they are excluded from builds

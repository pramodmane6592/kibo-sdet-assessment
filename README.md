# Kibo SDET Challenge — Interviewer Guide

> **This repo is internal.** Candidates receive a packaged zip that excludes this README, interviewer notes, and scripts.

## Purpose

A take-home + live-interview assessment designed to evaluate SDET candidates on:

- **Test architecture** — framework design, patterns (Builder, Fluent API), isolation
- **API testing** — HTTP client management, async polling, schema validation
- **Code quality** — readability, maintainability, idiomatic C#
- **Observability** — logging, diagnostics, custom handlers
- **GenAI fluency** — effective use of AI tools (Copilot, ChatGPT, Claude, etc.)

## Repo Structure

```
├── CANDIDATE_INSTRUCTIONS.md   ← What candidates see (included in zip)
├── SETUP.md                    ← Quick-start for candidates (included in zip)
├── INTERVIEWER_NOTES.md        ← Live interview tasks, rubric, scorecard (NOT in zip)
├── README.md                   ← You are here (NOT in zip)
├── scripts/
│   └── package-for-candidate.sh  ← Builds the candidate zip (NOT in zip)
├── src/
│   ├── Kibo.MockApi/           ← ASP.NET Core API (runs on port 5000)
│   └── Kibo.TestingFramework/  ← Empty project — candidate builds this out
├── tests/
│   └── Kibo.LegacyTests/      ← Intentionally bad tests — candidate refactors
├── Kibo.SDET.Challenge.sln
└── global.json
```

## Quick Start (for Interviewers)

```bash
# 1. Build
dotnet build Kibo.SDET.Challenge.sln

# 2. Run the mock API
dotnet run --project src/Kibo.MockApi

# 3. Run the legacy tests (they should pass as-is)
dotnet test tests/Kibo.LegacyTests

# 4. Package a zip for a candidate
./scripts/package-for-candidate.sh
#    → outputs dist/Kibo-SDET-Challenge-YYYYMMDD.zip
```

## What Candidates Do (Take-Home)

| Task | Summary | Time Estimate |
|------|---------|---------------|
| 1 | Refactor legacy tests into TestingFramework | ~30 min |
| 2 | Build a fluent `OrderBuilder` | ~20 min |
| 3 | Replace `Thread.Sleep` with async polling/retry | ~25 min |
| 4 | Add edge-case & negative tests | ~20 min |
| 5 | Document 5 GenAI prompts used across all tasks | ~15 min |
| 6 | Add a logging `DelegatingHandler` for observability | ~20 min |

**Expected completion: 2–3 hours** (all 6 tasks required).

## Live Interview (45–60 min)

See [INTERVIEWER_NOTES.md](INTERVIEWER_NOTES.md) for the full guide, including:

- **Warm-up questions** — ease in, gauge AI comfort
- **Core live tasks (pick 2)** — Custom assertion helper, test isolation, exponential backoff, schema validation, AI-assisted implementation
- **Quick tasks (pick 1)** — Env config, test categorization, negative builder validation
- **Discussion questions** — Architecture, debugging, CI/CD, AI/LLM usage
- **Scoring rubric** — 1–5 per dimension with strong/weak signal tables
- **Scorecard template** — Copy-paste into your notes

## Scoring Overview

| Rating | Take-Home | Live Coding | AI Fluency |
|--------|-----------|-------------|------------|
| **Strong Hire** | All 6 tasks clean, polished | Solves 2 core + 1 quick with minimal hints | Natural AI integration, critical evaluation |
| **Hire** | Tasks 1–5 solid, Task 6 partial | Solves with light coaching | Uses AI tools, explains trade-offs |
| **Borderline** | Tasks 1–4 done, gaps in 5–6 | Needs heavy coaching on 1+ task | Limited AI use or over-reliance |
| **No Hire** | Incomplete or copy-paste quality | Cannot progress with coaching | Cannot articulate AI usage |

## Key Technical Notes

- **API quirk:** Orders start as `Pending` → auto-transition to `ReadyForFulfillment` after 5 seconds (fire-and-forget `Task.Run` in the controller). This is what Task 3 tests.
- **Tenant header:** All API calls require `X-Tenant-Id` header or return `400`.
- **In-memory store:** `OrderStore` uses a static `ConcurrentDictionary` — no persistence, resets on restart.
- **Framework:** .NET 10, xUnit 2.9.3, no database.

## Packaging for Candidates

```bash
./scripts/package-for-candidate.sh
```

The script creates a zip in `dist/` that **excludes**:
- `INTERVIEWER_NOTES.md`, `README.md`
- `scripts/`, `dist/`, `.git/`, `.gitignore`
- Build artifacts (`bin/`, `obj/`)
- IDE configs (`.vs/`, `.vscode/`, `.idea/`)

Hand the candidate the zip. [SETUP.md](SETUP.md) is included inside it with unzip + build instructions.

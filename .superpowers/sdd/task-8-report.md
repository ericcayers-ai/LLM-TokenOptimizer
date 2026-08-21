# Task 8: AgencyAgentsInstaller — Report

## Status: DONE

## Commits
_(pending — will be committed after this report)_

## What Was Built

1. **AppConfig.cs** — Added two new properties:
   - `bool AgencyAgentsCloned` — tracks whether the agency-agents repo has been shallow-cloned
   - `List<string>? TickedAgencyAgents` — tracks which agent slugs are ticked for sync

2. **AgencyAgentsInstaller.cs** — New installer with three main operations:
   - `EnsureClonedAsync()` — shallow-clones `msitarzewski/agency-agents` into `~/.tokenoptimizer/agency-agents`, pull --ff-only if present, delete+reclone on failure
   - `ListAvailableAgentsAsync()` — parses `divisions.json` + `*.md` files with hand-rolled YAML frontmatter extraction (no YAML library dependency)
   - `SyncTickedAgentsAsync(tickedSlugs)` — copies ticked `.md` files to `{claudeConfigDir}/agents/`, tracks in `.agency-agents-synced.json`, removes unticked agents that were previously synced

3. **AgencyAgentsInstallerTests.cs** — 9 tests covering:
   - Frontmatter parsing: standard, missing, quoted values
   - Sync manifest bookkeeping: copy ticked, remove unticked, empty tick list, missing repo/divisions.json

## Test Summary
All 146 tests pass (74 Providers + 46 Core + 26 App). The 9 new AgencyAgentsInstaller tests use temp directories with no real git/network calls.

## Concerns
None — the implementation follows the same best-effort patterns as CompanionToolingInstaller (missing git → returns false, clone failure → returns false, never blocks).

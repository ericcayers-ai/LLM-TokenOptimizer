# Skill Observation Log

Observations captured during task-oriented work. Each entry identifies a
potential skill improvement or new skill opportunity.

**Status key:** OPEN = not yet actioned | ACTIONED = skill updated/created |
DECLINED = user decided not to pursue

---

## 2026-08-25

### Observation 1: Merge branches without .gitattributes eol enforcement causes false test failures on Windows

**Date:** 2026-08-25
**Session context:** Merging feature/opensandbox-master-layer into main; after merge, dotnet test showed 3 failing golden tests comparing generator output (LF) against raw-string literals in a .cs file, with a diff at CRLF boundaries.
**Skill:** git-workflow-and-versioning
**Type:** open-source
**Phase/Area:** Merge / post-merge verification

**Issue:** A .cs file with multi-line raw-string literals (golden test fixtures) had no `.gitattributes` `eol=lf` rule. With `core.autocrlf=true` on Windows, checkout silently converted the string content to CRLF, causing the test to fail against LF-only generator output. The failure looked like a real regression from the merge but was purely a line-ending artifact of the local checkout.

**Suggested improvement:** When a repo's test suite embeds golden/fixture literals with strict newline expectations (LF-only assertions, golden files, snapshot tests), add explicit `.gitattributes` `eol=lf` rules for the relevant file types before those tests are trusted cross-platform. The git-workflow-and-versioning skill's merge-verification step should include "run tests after merge, and if line-ending diffs appear in a failure, check .gitattributes before assuming a code regression."

**Principle:** On Windows with `core.autocrlf=true`, any test relying on exact byte/newline comparison against source-embedded fixtures is fragile unless `.gitattributes` pins line endings for that file type. This applies to any repo mixing cross-platform tooling with fixture-based tests, not just this one.

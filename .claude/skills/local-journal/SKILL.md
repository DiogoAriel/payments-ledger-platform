---
name: local-journal
description: Keep the git-ignored _local folder up to date with memory and an operations history. Use at the start of every session in this repository, before any change, and again when the work is done.
---

# Local journal

`_local/` is the working memory of this repository. It is in `.gitignore` and never leaves the machine.

```
_local/
├── memory.md              # facts and decisions that stay true across sessions
└── history/
    └── YYYY-MM-DD.md      # what was done that day, in order, with outcomes
```

## At the start of a session

1. Read `_local/memory.md` and the newest file in `_local/history/`.
2. Create today's history file if it does not exist, with a heading and the goal stated by the user.

## During the work

Append one line per operation that changed something: file edited, command run with its result, decision taken and why. Short, factual, in the order it happened.

## At the end

1. Add an "Outcome" section: what is done, what is not, what was verified (paste the test summary).
2. Move anything that will still be true next week into `_local/memory.md` (conventions, gotchas, environment facts, pending questions).

## Format

```
# 2026-10-14 — <goal>

- 14:02 build failed: NU1902 OpenTelemetry.Exporter 1.9.0 vulnerable → bumped to 1.19.1
- 14:20 SQLite cannot ORDER BY DateTimeOffset → DateTimeOffsetToBinaryConverter when IsSqlite()
...

## Outcome
- Done: ...
- Not done: ...
- Verified: 36 tests passed (24 unit, 12 integration)
```

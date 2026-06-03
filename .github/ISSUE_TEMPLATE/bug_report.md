---
name: Bug report
about: A parser/deploy/CLI bug — or SQL where pgproj and PostgreSQL disagree
title: "[bug] "
labels: bug
---

<!-- Thanks for helping improve CodeCleaners.PgProj. Please fill in the sections below. -->

## What happened

<!-- One or two sentences: what went wrong. -->

## Minimal SQL to reproduce

```sql
-- The smallest snippet that shows the problem.
```

## Command & output

```
$ pgproj <command> ...
<paste the output — build/validate print the offending file:line>
```

## Expected vs. actual

- **Expected:** <!-- e.g. parses cleanly / deploy plan X / rejected with error Y -->
- **Actual:** <!-- what pgproj actually did -->

## Does real PostgreSQL agree? (if known)

<!-- Does PostgreSQL accept or reject this SQL? Paste the server error + SQLSTATE if it errors.
     e.g. ERROR: 42601 syntax error at or near "..."  — this pins down who is wrong. -->

## Environment

- **Target PostgreSQL version:** <!-- 16 / 17 / 18 -->
- **pgproj commit / version:**
- **OS:**

## Additional context

<!-- Anything else: the .pgproj manifest, related objects, screenshots, etc. -->

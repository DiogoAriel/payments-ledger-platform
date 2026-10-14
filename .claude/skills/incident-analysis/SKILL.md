---
name: incident-analysis
description: Structured root-cause analysis for a production incident, a flaky test or an unexplained failure. Use when asked to "investigate", "find the root cause", "why did this fail" or when a test fails for a reason you cannot state in one sentence.
---

# Incident analysis

Measure before changing. A fix without a reproduced cause is a guess.

## Steps

1. **State the symptom precisely.** What was observed, where (log line, test name, HTTP status), when, how often. Copy the exact message; do not paraphrase.
2. **Reproduce.** Write the smallest test or script that shows the failure. If it cannot be reproduced, say so and stop at "hypotheses" below.
3. **Instrument.** Add logs, counters or assertions around the suspected area. Run again. Collect the numbers.
4. **Narrow.** Bisect by commit, by layer (API → handler → domain → persistence), or by input. One variable at a time.
5. **Name the cause.** One sentence: "X happens because Y". If there are two candidates, keep instrumenting until one is left.
6. **Fix and prove.** The reproduction from step 2 must now pass, and the full test suite must stay green.
7. **Prevent.** Add the test that would have caught it. If the cause was a missing invariant, move it into the domain.

## Record

Append to `_local/history/<today>.md`:

```
## Incident: <short title>
- Symptom:
- Reproduction:
- Measurements (before):
- Root cause:
- Fix:
- Measurements (after):
- Prevention (test/invariant added):
```

If the incident changed a design decision, add an ADR in `docs/adr/`.

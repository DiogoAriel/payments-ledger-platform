---
name: adversarial-review
description: Review a diff as a hostile reviewer would, looking for money-losing bugs in a ledger. Use before presenting any change, and whenever asked to "review", "check" or "audit" code in this repository.
---

# Adversarial review

You are not confirming the change works. You are trying to make it lose money, double-post, or leak.

## Procedure

1. Read the diff with `git diff` (or the range asked). List every file touched.
2. For each change, ask the questions below. Write down a concrete failing input for every "maybe".
3. Try to prove each suspicion with a test. A suspicion that cannot be turned into a failing test is reported as "unverified".
4. Report only what survived: file, line, the input that breaks it, the consequence. No praise, no style remarks unless they hide a bug.

## Questions, in order of damage

- **Balance integrity:** can this path change a balance without a journal entry? Can an entry be persisted unbalanced? Can a line have a non-positive amount?
- **Idempotency:** what happens when the same request arrives twice, concurrently? Is the reference checked before posting and enforced by the unique index?
- **Atomicity:** is there any write outside `UnitOfWork.Commit`? Can the outbox row be saved without the entry, or the entry without the balances?
- **Concurrency:** two transfers on the same account at the same time: does the version token reject one, and does the API answer 409 so the client retries?
- **Webhooks:** can a request pass with a wrong secret, an old timestamp, a reused nonce, a different provider's secret? Is the compared body the same bytes that were signed? Is the error response revealing which check failed?
- **Money:** any `double`/`float`? Any rounding not governed by the currency's minor units? Any arithmetic across currencies?
- **Validation vs. domain:** does the validator reject what the domain would also reject? If not, which one is the source of truth and is the HTTP status right (400 vs 422)?
- **Secrets and logs:** does any log line carry a secret, a signature, a full webhook body? Does any config default to a real-looking credential?
- **Tests:** does a test assert the *absence* of side effects on the failure path (no entry, no event, no commit)?

## Output format

```
## Findings (most severe first)
1. <file:line> — <what breaks> — input: <concrete> — consequence: <money/duplicate/leak/outage>
...
## Unverified suspicions
- ...
## Verified safe
- <one line per risky area you checked and could not break>
```

Then append a dated entry to `_local/history/<today>.md` with the findings and what was fixed.

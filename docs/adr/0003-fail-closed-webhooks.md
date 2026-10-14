# ADR 0003: Fail-closed webhook authentication with HMAC, timestamp and nonce

**Status:** accepted · **Date:** 2026-10-14

## Context

Payment providers notify settlements by calling our endpoint. A forged or replayed notification
would credit money that was never received. Providers retry deliveries, so the same event can
legitimately arrive more than once.

## Decision

A webhook is processed only when all of the following hold, checked in this order:

1. the provider name in the URL is configured with a secret;
2. the `X-Signature`, `X-Timestamp` and `X-Nonce` headers are present;
3. the timestamp is within the provider's tolerance window (default 5 minutes);
4. the HMAC-SHA256 of `"{timestamp}.{body}"` with the shared secret matches the signature,
   compared in constant time, over the exact bytes received;
5. the nonce has never been seen for that provider (registered only after the signature is valid).

Any failure answers `401` with no detail; the reason is logged server-side. Business idempotency is
separate: the provider's `externalId` becomes the ledger reference, so a redelivery with a new nonce
is accepted and answered with the original result instead of posting again.

## Consequences

- Replay of a captured request fails on the nonce; replay after the window fails on the timestamp;
  tampering fails on the signature.
- Clock skew between provider and platform must stay inside the tolerance; it is configurable per provider.
- Nonces are stored with an expiry so the table can be pruned after the window.

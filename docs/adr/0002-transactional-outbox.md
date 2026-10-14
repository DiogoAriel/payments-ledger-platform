# ADR 0002: Transactional outbox for integration events

**Status:** accepted · **Date:** 2026-10-14

## Context

Downstream systems (notifications, analytics, reconciliation) need to know when an entry is posted.
Publishing to Kafka inside the request has two failure modes: the entry commits and the publish fails
(consumers never learn about it), or the publish succeeds and the commit fails (consumers learn about
money that never moved).

## Decision

The handler writes the event as a row in `outbox_messages` inside the same database transaction as
the entry. A background relay (`OutboxPublisherService`) reads unpublished rows in order, publishes
them and marks them published. The Kafka producer is idempotent with `acks=all`.

## Consequences

- Delivery is at-least-once. Every event carries the entry id, and consumers must be idempotent.
- There is a small lag (poll interval, 2 s by default) between commit and publication.
- Without Kafka configured the relay logs the events, which keeps local runs and tests simple.
- Failed publications keep their error on the row and are retried; nothing is lost silently.

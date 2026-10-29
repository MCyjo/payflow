# 0001. Publish events through a transactional outbox

**Status:** Accepted

## Context
Placing an order has to do two things: save the order and tell the Payment Service. They cannot be one transaction,
because the database and Service Bus do not share one. Done in either order, a crash between the two steps breaks
something:

- insert first, then publish: the order exists and nobody ever pays for it;
- publish first, then insert: a payment is started for an order that does not exist.

## Decision
The `OrderCreated` event is written to an `OutboxMessages` table **in the same database transaction** as the order. A
background relay (`OutboxPublisherService`) reads unsent rows and publishes them, using the row id as the Service Bus
`MessageId`.

The relay claims rows with `SELECT ... FOR UPDATE SKIP LOCKED`, so several replicas can drain the table without
publishing a row twice. It marks a row as sent only after the broker accepted it.

## Consequences
- **Good:** an order and its event commit or fail together. Tested with the database, not mocked: a failed publish is
  retried and its error recorded, a crash after sending re-publishes the *same* `MessageId`, and two relays publish
  every one of 100 rows exactly once.
- **Delivery is at-least-once.** A crash after the broker accepted a message but before the row is marked sent
  publishes it again. That is why consumers are idempotent ([0002](0002-idempotent-consumers.md)) and the topic has duplicate
  detection.
- **Latency:** an event waits up to about a second for the next poll.
- **Cost found in production:** polling every second per replica produced about 40,500 telemetry records an hour and used
  up the daily Log Analytics cap in about 11 hours. The poll's spans are no longer exported (`OutboxPollFilterProcessor`).
  That the volume dropped afterwards is **not yet measured**, because the workspace stayed over its cap.

## Alternatives considered
- **Change data capture (Debezium):** no polling, but one more moving part to run for a four-service system.
- **PostgreSQL `LISTEN/NOTIFY`:** would remove the polling latency and load; a reasonable next step.
- **Publish inside the transaction and hope:** the problem this decision exists to avoid.

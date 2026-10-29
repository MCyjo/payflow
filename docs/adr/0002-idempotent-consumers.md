# 0002. Make every consumer idempotent, in three layers

**Status:** Accepted

## Context
Delivery is at-least-once ([0001](0001-transactional-outbox.md), and Service Bus redelivers after any failed or slow handler). For a
payment the worst outcome is charging twice. "The same message arrives twice" is not an edge case here, it is normal.

## Decision
The Payment Service defends against a repeated `OrderCreated` with three independent layers, because each one covers a
case the others cannot:

1. **Inbox table (`ProcessedMessages`)** keyed by `MessageId`. A message that was fully handled is skipped. It is written
   in the same transaction as the payment state and the outgoing event.
2. **One payment per order**, enforced by a unique index on `Payments.OrderId`. A duplicate event with a *different*
   `MessageId` (for example one re-published after a crash) still finds the existing payment instead of creating a second.
3. **Provider idempotency key.** The charge request carries `Idempotency-Key = payment id`. If the service crashes after
   the provider took the money but before it saved the result, the retry reuses the same payment and the same key, and the
   provider returns the original result.

The same idea applies elsewhere: `POST /api/orders` honours an `Idempotency-Key` header (concurrent requests with one key
create one order), webhook handling is idempotent, and `Confirm`/`Reject`/`Cancel` on an order are no-ops when already applied.

## Consequences
- **Tested against a real database, not only in unit tests:** 10 parallel messages for one order give one payment, one
  provider charge and one `PaymentCompleted`; the redelivered messages succeed afterwards without charging again.
- A concurrent duplicate can lose a unique-index race and fail once. The broker redelivers it and it then finds the existing
  payment. This is deliberate: retrying is simpler and safer than handling every race inline.
- More tables and more code than "just process the message". For money, that is the price.

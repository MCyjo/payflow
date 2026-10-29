# 0004. Choreography instead of an orchestrated saga

**Status:** Accepted. **The compensating action is not built.**

## Context
An order moves through several steps owned by different services: created, paid, confirmed or rejected. Either a central
orchestrator drives the steps, or each service reacts to events and emits its own.

## Decision
**Choreography.** Order Service emits `OrderCreated`; Payment Service reacts and emits `PaymentCompleted` or
`PaymentFailed`; Order Service reacts to those and confirms or rejects the order. There is no orchestrator and no shared
workflow state.

With three steps and one failure branch, a coordinator would be more machinery than the problem needs, and each service
stays ignorant of the others.

## Consequences
- Simple to follow and to test end to end: every outcome is covered through the real broker (success, decline, flaky
  provider, async webhook, provider down, duplicate webhook).
- The flow is implicit: it exists only in the combination of subscriptions. The architecture document draws it so it can be
  read in one place.
- **Known gap, stated plainly:** if the customer cancels while the payment is in flight and the payment then completes,
  Order Service correctly keeps the order `Cancelled` (tested), but **nothing refunds the money**. A refund would be a
  compensating step (`PaymentCompleted` for a cancelled order triggers `RefundRequested`). It is documented, not built.
- With more steps (inventory, shipping) and compensation in several directions, an orchestrator (a saga state machine)
  would start to pay for itself. This decision should be revisited then.

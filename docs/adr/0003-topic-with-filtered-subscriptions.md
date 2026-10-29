# 0003. One topic with filtered subscriptions for events

**Status:** Accepted

## Context
Several parties care about the same event: `OrderCreated` starts a payment, and a payment result must reach the Order
Service (to change the order), the notification function (to send an email) and an audit trail. Publishers should not know
who listens.

## Decision
All domain events go to one topic, `domain-events`. Each consumer has its own **subscription** with a SQL filter on the
message subject (which the publisher sets to the event type):

| Subscription | Filter |
|---|---|
| `payment-service` | `OrderCreated` |
| `order-service` | `PaymentCompleted`, `PaymentFailed` |
| `notification-function` | `PaymentCompleted`, `PaymentFailed` |
| `audit` | none: receives every event |

Every subscription has its own dead-letter queue and `MaxDeliveryCount = 5`. The topic has duplicate detection on
`MessageId`.

## Consequences
- Adding a consumer is a new subscription; no publisher changes.
- **A real trap, found by deploying:** every subscription is created with a built-in `$Default` rule that accepts
  everything. Adding a rule next to it does not filter anything, and *replacing* `$Default` (an ARM deployment, or
  `az ... rule update`) **deletes it and leaves the subscription with no rule, so it receives nothing**. Adding a rule with a
  different name makes the broker drop `$Default` itself, which is what Terraform does now. I first shipped the
  replacement approach, saw the subscriptions go silent on the live namespace, reproduced it on throwaway subscriptions
  and fixed it.
- **Not used:** a `payment-commands` queue is provisioned (and configured with a dead-letter policy and duplicate
  detection) but no code sends to it. The commands-versus-events split in the original design was not built, because the
  only command here, "start a payment", is better expressed as the consequence of an event.

# Architecture decision records

Short records of the decisions that shaped PayFlow: the context, what was decided, and what it costs. Several were
changed by something the real system or the tests revealed; those records say so.

| # | Decision | Status |
|---|---|---|
| [0001](0001-transactional-outbox.md) | Publish events through a transactional outbox | Accepted |
| [0002](0002-idempotent-consumers.md) | Make every consumer idempotent, in three layers | Accepted |
| [0003](0003-topic-with-filtered-subscriptions.md) | One topic with filtered subscriptions for events | Accepted |
| [0004](0004-choreography-over-orchestration.md) | Choreography instead of an orchestrated saga | Accepted, compensation not built |
| [0005](0005-optimistic-concurrency.md) | Optimistic concurrency with PostgreSQL `xmin` | Accepted |
| [0006](0006-identity-without-secrets.md) | Identity instead of secrets everywhere | Accepted |
| [0007](0007-functions-on-flex-consumption.md) | Azure Functions on Flex Consumption | Accepted, deployment not working yet |
| [0008](0008-azure-sdk-over-a-messaging-framework.md) | Azure SDK directly instead of MassTransit or Wolverine | Accepted |
| [0009](0009-trace-context-through-messages.md) | Carry the W3C trace context inside messages | Accepted |

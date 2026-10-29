# 0008. Azure SDK directly instead of MassTransit or Wolverine

**Status:** Accepted

## Context
Frameworks such as MassTransit and Wolverine provide an outbox, an inbox, retries and consumers out of the box, and are the
sensible choice for most production .NET systems.

## Decision
Use `Azure.Messaging.ServiceBus` directly and implement the outbox relay, the inbox, the subscription consumer, dead-letter
handling and trace propagation in a small shared library (`PayFlow.Messaging`, a few hundred lines).

The point of this project is to show that the mechanisms are understood, not only configured. Each piece is small enough to
read in one sitting, and each is covered by tests against a real database and the real Service Bus emulator.

## Consequences
- Full control over the behaviours that matter for correctness (when a message is completed, abandoned or dead-lettered; what is
  committed in which transaction), and nothing hidden behind conventions.
- More code to own. A team shipping a real product should use a framework and spend the time elsewhere; the architecture
  stays the same, only the library underneath changes.
- Business code depends on two small interfaces (`IOutbox`, `IInbox`) and never on the SDK, which is enforced by an
  architecture test.

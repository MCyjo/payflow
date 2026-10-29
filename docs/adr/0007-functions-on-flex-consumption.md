# 0007. Azure Functions on Flex Consumption

**Status:** Accepted. **The deployed function app does not work yet** (see Consequences).

## Context
Notifications ("payment confirmed" / "payment failed" emails) and an audit trail are natural event-driven functions: they
react to messages and need no always-on process. They are implemented as an Azure Functions app (isolated worker, .NET 10)
with two Service Bus triggers.

## Decision
Host the app on the **Flex Consumption** plan (`FC1`), with a system-assigned identity and an identity-based Service Bus
connection (no connection string).

The first choice was the Linux Consumption plan (`Y1`). Azure Resource Manager **accepted** that configuration, and I wrote
down that it worked. It did not: .NET 10 cannot run on Linux Consumption, the host never started and deployment failed
with `Sync Trigger ... malformed content`. Acceptance of a setting is not proof that it works. Microsoft also points new
apps at Flex Consumption, because Linux Consumption is being retired.

## Consequences
- The functions themselves are covered by unit tests (the notification text, handling of unsupported and malformed
  messages) and build and publish with both triggers discovered.
- **Open problem:** on Flex Consumption the deployment succeeds (Kudu reports success), but the host answers `502` on its
  administration endpoints, lists only the platform's own `WarmUp` function, and the subscriptions' messages are not
  consumed. The cause is **unknown**: the platform discards instance logs, and the workspace had also hit its daily cap, so
  there was no telemetry to read. The next step is the portal's log stream or "Diagnose and solve problems".
- Until that is fixed, notifications and audit run only in the local setup.

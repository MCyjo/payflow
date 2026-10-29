# 0009. Carry the W3C trace context inside messages

**Status:** Accepted

## Context
One order touches the Order API, PostgreSQL, Service Bus, the Payment Service, the provider and the Order Service again.
Without propagation these are separate, unrelated traces, and following a payment means searching logs by hand.

## Decision
Put the W3C `traceparent` in each message's application properties. The consumer starts its span as a child of the
publisher's span. The same value is also written as `Diagnostic-Id`, the property the Azure Service Bus tooling uses for
correlation, so that the notification function can join the trace. **That last part is unverified**: the function app does
not run yet ([0007](0007-functions-on-flex-consumption.md)).

The outbox stores the trace context next to the event, so the relay, which runs later on another thread, still publishes
**inside the original request's trace**.

A shared `ServiceDefaults` library configures OpenTelemetry for every service. Exporters are chosen by configuration:
OTLP (Aspire Dashboard locally), Azure Monitor when `APPLICATIONINSIGHTS_CONNECTION_STRING` is set, or the console.

## Consequences
- **Verified in Azure:** one order appears as a single trace across `order-service`, `payment-service` (including the call
  to the provider) and back to `order-service` in Application Insights.
- Unit tests check the round trip (publisher span, message, consumer span share a trace id and parent/child ids).
- Telemetry volume needs care: the outbox poll once filled the daily cap ([0001](0001-transactional-outbox.md)). Idle polling is now excluded
  from export.

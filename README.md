# PayFlow

Event-driven payment processing platform built with .NET, Azure Service Bus, Azure Functions and Kubernetes.

> Work in progress. See `docs/` for the roadmap.

## Running locally

```bash
cp .env.example .env      # set local-only passwords
docker compose up -d
```

| Component | Address |
|---|---|
| PostgreSQL | `localhost:5432` (databases `orders`, `payments`) |
| Service Bus emulator | `localhost:5672` |
| Aspire Dashboard (traces/logs) | http://localhost:18888 |

### Running the services

```bash
dotnet run --project src/OrderService/Api --launch-profile http                  # :5101 (set ASPNETCORE_URLS)
dotnet run --project src/PaymentService/Api                                      # :5102
dotnet run --project src/PaymentProviderSimulator                                # :5200
```

Set `ASPNETCORE_ENVIRONMENT=Development` and `ASPNETCORE_URLS=http://localhost:<port>` for each. In Development the
services apply their EF migrations on startup.

Create an order and pick the payment outcome with a test token:

```bash
curl -X POST http://localhost:5101/api/orders -H "Content-Type: application/json" \
  -d '{"customerId":"<guid>","amount":149.99,"currency":"EUR","paymentToken":"tok_success"}'
curl http://localhost:5102/api/payments/by-order/<orderId>
```

| Token | Provider behaviour | Demonstrates |
|---|---|---|
| `tok_success` | 200 | happy path |
| `tok_declined` / `tok_insufficient` | 402 | business failure -> `PaymentFailed` |
| `tok_flaky` | 503 twice, then 200 | HTTP retries with idempotency key |
| `tok_500` | always 500 | retries -> broker redelivery -> final attempt fails the payment |
| `tok_timeout` | hangs 30 s | per-attempt timeout, circuit breaker |
| `tok_async` / `tok_async_declined` | 202, result via signed webhook | webhook handling |
| `tok_duplicate_webhook` | 202, same webhook delivered twice | idempotent webhook |

### Notification / audit functions

`src/NotificationFunction` is an Azure Functions (isolated worker) app with two Service Bus triggers:
`PaymentNotificationFunction` (subscription `notification-function`, logs the "email") and `AuditFunction`
(subscription `audit`, receives every event). Running it locally needs
[Azure Functions Core Tools](https://learn.microsoft.com/azure/azure-functions/functions-run-local) and Azurite
(included in `docker-compose.yml`):

```bash
cp src/NotificationFunction/local.settings.json.example src/NotificationFunction/local.settings.json
cd src/NotificationFunction && func start
```

### Order lifecycle

`POST /api/orders` -> `OrderCreated` (outbox) -> Payment Service charges the provider -> `PaymentCompleted` /
`PaymentFailed` -> Order Service sets the order to `Confirmed` / `Rejected`. A cancelled order stays cancelled
when a payment result arrives late; refunding that payment is a planned compensating action.

Service Bus emulator connection string:
`Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;`

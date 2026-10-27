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

Service Bus emulator connection string:
`Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;`

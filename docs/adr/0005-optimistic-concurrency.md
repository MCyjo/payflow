# 0005. Optimistic concurrency with PostgreSQL `xmin`

**Status:** Accepted

## Context
Aggregates (`Order`, `Payment`) are loaded, changed in memory and saved. Without a concurrency check, two writers that
loaded the same row both succeed and the later one silently overwrites the earlier. The unit tests could not show this,
because it needs real concurrent transactions.

When the integration tests for duplicate webhooks were written, the problem was concrete: ten copies of the same
"payment completed" webhook delivered in parallel produced **ten `PaymentCompleted` events**, because each saw the payment as
still processing.

## Decision
Map PostgreSQL's system column `xmin` (the id of the transaction that last wrote the row) as an EF Core concurrency token on
`Order` and `Payment`. A stale write now fails with a concurrency exception instead of overwriting.

The exception is translated at the edge of each use case:
- the webhook handler answers `409 Conflict`, so the provider retries and the retry sees the final state;
- cancelling an order that was just confirmed reports a conflict instead of an error;
- a message consumer lets the exception propagate, so the broker redelivers and the retry sees the final state.

## Consequences
- Verified by deleting the token and watching the test fail with 10 events instead of one.
- No extra column to maintain: `xmin` already exists. The cost is a PostgreSQL-specific mapping.
- Conflicts surface as retries rather than being resolved inline. For the traffic here that is the simpler design.

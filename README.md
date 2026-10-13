# Order State Machine

A .NET 10 service for checkout orders, where failure recovery depends on the stage that failed.

```
Initialized ──authorize──┬─ declined ─────────────────────► Rejected
                         └─ ok ─► PaymentAuthorized ──complete──┬─ ok ──────────────────► Complete
                                                                ├─ failed, void ok ─────► Cancelled
                                                                ├─ failed, void failed ─► NeedsAttention
                                                                └─ outcome unknown ─────► NeedsAttention
```

## Design

It's the State pattern. `Order` holds an `IOrderState` and forwards `AuthorizePaymentAsync` and
`CompleteAsync` to it. Each state is a class that overrides only the actions it allows (for example, you can't complete an order that is in initialized state - it has to be payment_authorized first).
Everything else throws, which the API returns as a 409. All the states are in
`Orders/OrderStates.cs`, so you can read the whole machine top to bottom, and all the recovery
logic lives in `PaymentAuthorizedState`.

One decision I'd defend: **a timeout is not a failure.** If completion times out, the tickets
may already have been issued, so voiding could hand them out for free. Only a definite failure
triggers the void. An unknown outcome goes to `NeedsAttention`, with the reason recorded in the
order's history and logged. If a timeout occurs when an order is initialized, that can safely be retried. The other statuses would go to `NeedsAttention`

Also:
- Every gateway call is cancelled after 5s. The request may already have reached the provider,
  so a timeout still counts as an unknown outcome.
- Actions are serialized per order, so concurrent requests can't double-authorize.
- Authorization retries are safe because the order ID is the idempotency key.

## Running it

```bash
dotnet test
```

```bash
dotnet run --project src/OrderStateMachine.Api --urls http://localhost:5199
```

Endpoints: `POST /orders`, `POST /orders/{id}/authorize`, `POST /orders/{id}/complete` and
`GET /orders/{id}`. In Development, Swagger UI is at `/swagger`.

The payment gateway and fulfillment are mocks. You can force failures with
`Stubs__DeclinePayment`, `Stubs__FailCompletion` and `Stubs__FailVoid`, for example:

```bash
Stubs__FailCompletion=true Stubs__FailVoid=true dotnet run --project src/OrderStateMachine.Api --urls http://localhost:5199
```

The VS Code launch configs cover each scenario under the debugger.

## Tradeoffs

- **In memory, no database.** Without row-level transactions or optimistic concurrency, a
  per-order `SemaphoreSlim` stops concurrent requests from double-authorizing or double-voiding.
  That only works on a single instance, and orders are lost on restart (infra constraint).
- **The state machine is the only idempotency guard.** A retried `authorize` or `complete` can't
  charge or void twice, because the retry finds the order already moved on and gets a 409. The
  client then has to `GET` the order to see what happened, instead of getting the original
  response back. A retried `POST /orders` creates a duplicate order, but no money has moved yet.
- **The void runs inline, with no durable record.** A crash between a failed completion and the
  void leaves the order in `PaymentAuthorized`, and a `complete` request can take up to about 10s.
- **Unknown outcomes go to a person.** This of course does not scale and is not user-friendly, at all.

## What I Would Do Differently

- Persist orders to a DB, use optimistic concurrency, and write void intents to an outbox so they
  survive a crash or restart.
- Accept an `Idempotency-Key` header on the POST endpoints and replay the stored response on a
  retry, so clients get the original result and creating an order can't duplicate.
- Retry voids with backoff before giving up to `NeedsAttention`.
- Reconcile unknown outcomes automatically by asking the provider for the status by idempotency
  key.
- Add a `resolve` action on `NeedsAttentionState` so operators can close out stuck orders.
- Make completion asynchronous. `complete` moves the order to a new `Completing` state, writes a
  `CompletionRequested` event through the outbox, and returns `202 Accepted`. A consumer runs
  fulfillment, retries and the void, and the client polls `GET /orders/{id}` or gets a push
  notification. The consumer is safe to run twice: it skips orders that are no longer
  `Completing`, and the order ID is the idempotency key with the provider. This also removes the
  10s request and the crash gap between a failed completion and the void.# order-state-machine

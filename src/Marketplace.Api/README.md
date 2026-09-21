# Marketplace Orders Service

Backend service for order checkout and stock reservation in a mini marketplace.
Built with .NET 10, PostgreSQL, Redis and Docker.

## How to run

```bash
docker compose up --build
```

The API starts on http://localhost:8080. The schema is created automatically on the first start.

Postgres and Redis are exposed on host ports 5433 and 6380 so they don't conflict with local installations.

To run the integration tests (Docker must be running):

```bash
dotnet test
```

To run the parallel orders scenario against the running stack:

```bash
./scripts/concurrency-test.sh
```

Expected output: 10 responses with 201 and 40 with 409, final stock 0.

## API

| Method | Path | Auth | Description |
|---|---|---|---|
| POST | /auth/register | no | Register a user |
| POST | /auth/login | no | Get a JWT |
| POST | /products | yes | Create a product |
| GET | /products/{id} | no | Get a product |
| POST | /orders | yes | Create an order, requires `Idempotency-Key` header |
| GET | /orders/{id} | yes | Get an order and its status |
| POST | /orders/{id}/pay | yes | Mock payment, pending to confirmed |
| POST | /orders/{id}/cancel | yes | Cancel a pending order and return stock |
| GET | /health | no | Health check |

Example:

```bash
curl -X POST http://localhost:8080/orders \
  -H "Authorization: Bearer <token>" \
  -H "Idempotency-Key: 3f1c7a52-order-1" \
  -H "Content-Type: application/json" \
  -d '{"items":[{"productId":1,"quantity":2}]}'
```

### Status codes

| Code | When |
|---|---|
| 201 | Order created, or the same request replayed with the same key (header `Idempotent-Replayed: true`) |
| 400 | Malformed body, missing required fields, missing `Idempotency-Key` |
| 401 | Missing or invalid token, wrong credentials |
| 404 | Order doesn't exist or belongs to another user |
| 409 | Not enough stock, order is already confirmed or cancelled, payment window expired |
| 422 | Invalid values, unknown product, key reused with a different request body |

## Decisions

### Things the task didn't specify

- Auth endpoints were added because JWT is required but there was no way to get a token.
- `POST /orders/{id}/pay` was added because orders must become confirmed after payment, but no payment endpoint was described. It's a mock.
- `GET /products/{id}` was added to have a real read path for caching and to check stock.
- Only pending orders can be cancelled. Cancelling a confirmed order would be a refund flow.
- Stock is decremented when the order is created and returned on cancel or expiration.
- Any authenticated user can create products. Roles are out of scope.

### Stack

I used Npgsql directly without Dapper, since the task says no ORM. All SQL is in the repositories and mapping is written by hand.

### Architecture

Controller, service, repository, in one project:

- Controllers only deal with HTTP: headers, user id from the token, status codes.
- Services hold business rules, transaction boundaries and cache invalidation.
- Repositories run SQL and map rows.

Without an ORM there is no unit of work, so the service opens the transaction and passes it to repository methods. The business operation decides what has to be atomic, so the service owns that decision.

I didn't add repository interfaces or MediatR. Services pass a real transaction to repositories, so mocking repositories wouldn't test anything useful. Correctness under concurrency can only be proven against a real database, so tests are integration tests with Testcontainers.

### Database

Schema: https://dbdiagram.io/d/6ab16671bd4073ff5130a548.

- Constraints back up validation. `CHECK (stock >= 0)` means even a bug in the application can't oversell.
- Money is `NUMERIC(12, 2)` and `decimal` in C#. No floating point.
- Order status is `VARCHAR` with a `CHECK` constraint instead of a PostgreSQL enum, because changing a check constraint later is simpler than altering an enum type.
- `UNIQUE (user_id, idempotency_key)` on `orders` is what makes idempotency work (see below). Keys are scoped per user.
- `expires_at` is stored on the order. A partial index `ON orders (expires_at) WHERE status = 'pending'` keeps the expiration job query cheap because the index contains only pending orders.
- `order_items` stores `unit_price` as a snapshot, since a product price can change after the order is placed.
- Primary keys are `bigint` identity. Sorting by `long` in C# and by `bigint` in SQL gives the same order, and deadlock prevention depends on that (see Concurrency).
- The schema is created by SQL files in `db/init`, which the Postgres image runs on an empty volume. This keeps the setup to one command with no extra tooling. The limitation is that the scripts don't run again on an existing volume, so for production I'd use a versioned migration tool like Flyway or DbUp.

### Concurrency

Stock is reserved with one conditional statement:

```sql
UPDATE products
SET stock = stock - @quantity, updated_at = now()
WHERE id = @productId AND stock >= @quantity
RETURNING price
```

`UPDATE` takes a row lock. A parallel transaction updating the same product waits until the first one commits or rolls back. Under `READ COMMITTED` (the default), PostgreSQL then evaluates the `WHERE` clause again against the latest committed row. If the stock is gone, zero rows are updated, nothing is returned, the transaction rolls back and the API returns 409. There is no gap between reading and writing stock, so 50 parallel requests against a stock of 10 give exactly 10 successful orders.

Why not the other options:

- In-process locks (`lock`, `SemaphoreSlim`) stop working with more than one instance.
- A Redis lock or a stock counter in Redis creates a second source of truth that can drift from the database.
- `SERIALIZABLE` isolation and optimistic concurrency with a version column are correct, but failed transactions need retries. Under 50 parallel requests most of them would fail and retry.
- `SELECT ... FOR UPDATE` and then `UPDATE` is also correct, but it's two round trips per item for the same result.

**Deadlocks.** If order A locks product 1 then 2, and order B locks 2 then 1, they wait for each other and PostgreSQL kills one of them. To prevent this, items are merged and sorted by `product_id` before reserving, and the cancel path reads items `ORDER BY product_id`. When every transaction locks rows in the same order, a cycle can't happen.

**Connections.** Each transaction waiting for a lock holds a connection. The default Npgsql pool (100) and PostgreSQL `max_connections` (100) are enough for this load. For much higher concurrency I'd add PgBouncer and tune pool sizes.

### Idempotency

Inside the create transaction, the order row is inserted before any stock is touched:

```sql
INSERT INTO orders (user_id, status, idempotency_key, request_hash, expires_at)
VALUES (@userId, 'pending', @idempotencyKey, @requestHash, now() + @paymentTimeout)
ON CONFLICT (user_id, idempotency_key) DO NOTHING
RETURNING id
```

- **Retry after the first request finished.** The conflict returns no id. The transaction rolls back without changes and the existing order is returned. Stock is not decremented again.
- **Identical requests at the same moment.** When a unique index sees a conflicting row from an uncommitted transaction, PostgreSQL makes the second insert wait. If the first transaction commits, the second one gets the conflict and returns the same order. If the first rolls back (for example, not enough stock), the second runs as a normal attempt. The unique index works as the lock, so no Redis lock is needed.
- **Same key, different body.** A SHA-256 hash of the normalized items (merged and sorted) is stored with the order. If the hash differs, the API returns 422.

A missing key returns 400, following the IETF draft for the `Idempotency-Key` header. A replay returns the same 201 response with the header `Idempotent-Replayed: true`.

Failed attempts are rolled back, so the key is not stored and a retry with the same key is evaluated again. That's safe because nothing was reserved.

Keys are stored forever right now. In production I'd allow reusing a key after a retention period (for example 24 hours).

### Order lifecycle

`pending` can become `confirmed` (payment) or `cancelled` (user cancel or expiration). Both final states are terminal.

Every status change is one guarded `UPDATE`:

```sql
UPDATE orders SET status = 'cancelled', updated_at = now()
WHERE id = @orderId AND status = 'pending' AND (@userId::bigint IS NULL OR user_id = @userId)
```

If two cancels race, or a cancel races with the expiration job, the second `UPDATE` waits for the row lock, sees the new status and matches nothing. Only the transaction whose update matched returns stock, so stock is returned exactly once.

Payment also checks `expires_at > now()`. The expiration job runs every 30 seconds, and without this check a user could pay for an order that already expired but wasn't cancelled yet. With it, the 15 minute limit is exact from the client side.

When a status change fails, the service reads the order to return a precise error: 404 if it doesn't exist or belongs to another user, 409 if it's already confirmed, cancelled or expired.

### Background job

`ExpiredOrdersJob` is a `BackgroundService` with a `PeriodicTimer` (30 seconds by default). Each run selects up to 500 expired pending orders and cancels them through the same service method as a user cancel, so there is only one code path that returns stock.

- Running several API instances is safe. Two instances can pick the same order, but the guarded update makes the second cancel a no-op.
- Each run is wrapped in `try/catch`. An unhandled exception in a `BackgroundService` stops the whole host, and a failed run is simply retried on the next tick.
- Stock of an expired order returns at most one interval after expiry. Payment is rejected exactly at expiry regardless of the job.

I didn't use Hangfire or Quartz because one periodic query doesn't need a scheduler. Redis keyspace expiration events were rejected because they are fire-and-forget and get lost if no subscriber is connected at that moment.

Both values are configurable in `appsettings.json` under `Orders`.

### Caching

Common caching strategies:

I used **cache-aside with TTL and invalidation after commit**.

| Key | Value | TTL | Invalidated when |
|---|---|---|---|
| `v1:product:{id}` | product | 5 min | order created, cancelled or expired |
| `v1:order:{id}` | order with items | 1 min if pending, 30 min if final | paid, cancelled or expired |

- **The cache never takes part in decisions.** Stock is always checked by the conditional `UPDATE` in PostgreSQL, and idempotency is checked by the unique index. The cached product is for reading only.
- **Keys are deleted after the transaction commits.** Deleting before commit would let a parallel read load the old row and put it back into the cache.
- **TTL limits a known race.** A reader can load old data, then a writer commits and deletes the key, then the reader stores the old data. The TTL limits how long that stale value can live. That's why pending orders, which still change, get a short TTL, and final orders, which never change, get a long one.
- **Redis failures are not errors.** Cache calls catch Redis exceptions, log a warning and fall back to the database. `AbortOnConnectFail = false` lets the API start even when Redis is down.
- **The `v1:` prefix** allows changing the cached format without flushing Redis.

### Authentication

- JWT signed with HS256. The key must be at least 32 bytes, which is checked at startup so a bad key fails immediately instead of on the first login.
- Passwords are hashed with ASP.NET Core `PasswordHasher` (PBKDF2 with HMAC-SHA512, a per-user salt and 100,000 iterations).
- `MapInboundClaims = false` keeps the standard `sub` claim name instead of the long .NET claim type URI.
- Orders of other users return 404 instead of 403, so order ids can't be probed.
- The key in `docker-compose.yml` is a development default and can be overridden with the `JWT_KEY` environment variable. In production it would come from a secret store.
- Access tokens last 60 minutes and there are no refresh tokens, so a token cannot be revoked before it expires. For production I'd add short-lived access tokens with rotating refresh tokens.

### Docker

- `docker compose up --build` starts PostgreSQL, Redis and the API. The API waits for both healthchecks.
- The Postgres healthcheck uses `pg_isready -h localhost`. During initialization the image runs a temporary server that only listens on a Unix socket, so a socket check reports ready before the schema exists. A TCP check only passes after init scripts finish.
- The API image is a multi-stage build: the SDK image compiles, the smaller ASP.NET runtime image runs it. It runs as the non-root `app` user and listens on 8080, the default for .NET images.

# OrionOnce roadmap

Current version: **0.2.1**. OrionOnce is an idempotency library for .NET: idempotency keys, request
fingerprinting, a pluggable idempotency store, ASP.NET Core middleware that replays a captured HTTP
response, and an `IdempotentExecutor` that runs an operation once and replays its typed result.

The plan below is grounded in the current code, not a wish list. Version milestones are targets, not
commitments; anything listed may change, ship differently, or be dropped. Dates assume the cadence
of the releases so far and will move with real work. For the shipped surface see the
[README](../README.md) and [FEATURES](FEATURES.md); for the full history see
[CHANGELOG.md](../CHANGELOG.md).

## Recently shipped

- **0.2.1 (2026-06-20)** Allocation-free fingerprinting. `RequestFingerprint.Compute` now assembles
  the hashed message in a single `ArrayPool`-rented buffer and writes the hex digest into a stack
  buffer, dropping per-call allocation to a constant 152 B independent of body size (versus ~8.7 KB
  for an 8 KB body before). The wire format is byte-for-byte unchanged.
- **0.2.0 (2026-06-19)** Idempotent execution outside HTTP. `IdempotentExecutor` runs an operation
  once under a key and replays its captured typed result on later duplicate calls, over the same
  `IIdempotencyStore`; result serialization is supplied by the caller through
  `IIdempotentResultCodec<TResult>`. `IIdempotencyStore.SweepAsync` purges expired entries and
  reports how many were removed, and the in-memory store gained a `TimeProvider`-based clock for
  testable expiry.
- **0.1.0 (2026-06-14)** Initial release: `IdempotencyMiddleware` / `UseOrionOnce()`, the
  `IIdempotencyStore` contract with `InMemoryIdempotencyStore`, `RequestFingerprint`,
  `IdempotencyOptions`, and outcome-tagged metrics through `IdempotencyDiagnostics`.

## Next

### 0.3.0 - durable and shared stores (target 2026 Q3)

Today the only shipped `IIdempotencyStore` is process-local, so multi-instance deployments must
write their own. This milestone closes that gap.

- A durable EF Core store (`Moongazing.OrionOnce.EntityFrameworkCore`) that persists keys, leases,
  and captured responses, with `AcquireAsync` kept atomic through a unique key constraint plus the
  appropriate row-locking, and `SweepAsync` implemented as a bulk delete of expired rows.
- Written guidance for implementing `IIdempotencyStore` over a distributed backend: how to keep the
  claim atomic across nodes (for example a Redis `SET key value NX PX`), how to represent the
  in-progress, completed, and mismatch states, and how to scope retention.
- A sample project showing OrionOnce against a real shared store, replacing the hand-written
  `RedisIdempotencyStore` sketch in the README with something runnable.

### 0.4.0 - response fidelity and policy (target 2026 Q4)

The middleware currently replays status, content type, and body, and treats anything below `5xx` as
cacheable. Both are reasonable defaults that some applications need to override.

- Capture and replay a configurable allowlist of response headers alongside the existing status,
  content type, and body, so headers such as `Location` survive a replay.
- A response-cacheability hook so an application can decide which responses are stored, rather than
  relying only on the built-in "anything below `5xx`" rule (for example to avoid caching a `409`).
- Configurable fingerprint scope, letting an application include or exclude the query string or
  selected headers when its notion of "the same request" differs from method + path + body.

### 0.5.0 - message-consumer ergonomics and observability (target 2027 Q1)

`IdempotentExecutor` already covers non-HTTP retries; this milestone makes it idiomatic for queue
consumers and improves what operators can see.

- Thin helpers for deriving a key and fingerprint from a message (delivery id or a caller-supplied
  business key plus the payload), so a RabbitMQ or similar consumer can guard a handler without
  hand-rolling the plumbing each time.
- Richer telemetry alongside the existing `oriononce.requests` counter: a replay-versus-execute
  latency histogram and optional `ActivitySource` spans for the acquire and replay paths, so a
  duplicate that was replayed is visible in a trace.
- Per-endpoint or attribute-based opt-in/opt-out for the middleware, so idempotency can be scoped
  more finely than the guarded-method set.

## Beyond

- A 1.0.0 release once the store contract, the response-fidelity policy surface, and the consumer
  helpers have settled, at which point the public surface stabilizes under semantic versioning.

If one of these matters to you, open an issue describing the use case. Concrete needs shape what gets
built and in what order.

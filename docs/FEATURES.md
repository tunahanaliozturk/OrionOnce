# OrionOnce features

A deep breakdown of what OrionOnce does and how each piece behaves. Everything here reflects the
`0.1.0` public surface in `src/Moongazing.OrionOnce`.

## The idempotency middleware

`IdempotencyMiddleware` (registered through `UseOrionOnce()`) is the entry point. For each request
it runs the following decision flow:

1. **Method gate.** If the request method is not in `IdempotencyOptions.Methods` (by default only
   `POST`, `PUT`, `PATCH`, `DELETE`), the middleware calls the next delegate and returns. Safe
   methods are never buffered or fingerprinted.
2. **Key extraction.** It reads the header named by `IdempotencyOptions.HeaderName`
   (default `Idempotency-Key`). A missing or whitespace-only value means "no key".
   - With `RequireKey = true`, a guarded request with no key is rejected `400 Bad Request`
     (outcome `missing_key`) and the handler never runs.
   - With `RequireKey = false` (the default), the request bypasses idempotency and is handled
     normally (outcome `bypassed`).
3. **Body buffering.** The request body is read into memory with `EnableBuffering`, capped at
   `MaxBodyBytes`. If the body exceeds the cap, the request is rejected `413 Payload Too Large`
   before any handler work. The buffered body is rewound so the handler can re-read it.
4. **Fingerprint + claim.** It computes the request fingerprint and calls
   `IIdempotencyStore.AcquireAsync(key, fingerprint)`. The returned lease drives the outcome:
   - `Acquired`: the handler runs once and its response is captured (see below).
   - `AlreadyCompleted`: the stored response is replayed with `Idempotency-Replayed: true`; the
     handler is skipped.
   - `InProgress`: a concurrent request already holds the key, so this one is rejected
     `409 Conflict`.
   - `FingerprintMismatch`: the key was used before with a different request, so this one is
     rejected `422 Unprocessable Entity`.

### Response capture

When a key is acquired, the middleware swaps the response body for a `MemoryStream`, runs the
handler, and then writes the captured bytes back to the real response stream. After the handler
completes:

- **Handler throws.** The original response stream is restored, the key is released via
  `ReleaseAsync`, and the exception is rethrown. Nothing is cached, so a retry re-runs the handler.
- **Handler returns `5xx`.** A `500`-or-greater status is treated as transient: the key is released
  and the response is not cached, so the same key can be retried.
- **Handler returns `<500`.** The status code, content type, and body are stored via
  `CompleteAsync` as a `CachedResponse`, ready to replay on the next request with the same key.

### What is replayed

A replay reproduces the original **status code**, **content type**, and **body** and adds the
`Idempotency-Replayed: true` header (`IdempotencyMiddleware.ReplayedHeader`). Other response
headers from the first call are intentionally **not** captured or replayed in this version.

## Request fingerprint

`RequestFingerprint.Compute(method, path, body)` is a pure static helper:

- Hashes the uppercased method, a newline, the path (the middleware passes path plus query string),
  another newline, and the raw body bytes.
- Returns a 64-character lowercase hex SHA-256 digest.
- Binds a key to a specific request so reusing a key for a different body or route is detectable.

Because the path is combined with the query string by the middleware, the same key used against
two different query strings is treated as a mismatch.

## Options

`IdempotencyOptions` is validated when `AddOrionOnce` runs (`HeaderName` non-empty; `Retention` and
`MaxBodyBytes` positive). Invalid options throw at startup rather than at request time.

| Option | Default | Notes |
|--------|---------|-------|
| `HeaderName` | `Idempotency-Key` | The request header that carries the key |
| `Retention` | `TimeSpan.FromHours(24)` | Retention window passed to the in-memory store |
| `Methods` | `POST`, `PUT`, `PATCH`, `DELETE` | A mutable case-insensitive set you can add to or clear |
| `RequireKey` | `false` | Whether a guarded request must carry a key |
| `MaxBodyBytes` | `1024 * 1024` (1 MiB) | Body size cap for buffering and fingerprinting |

## Storage contract

`IIdempotencyStore` has three methods, all cancellable:

- `Task<IdempotencyLease> AcquireAsync(string key, string fingerprint, CancellationToken)` —
  atomically claim a key or report `AlreadyCompleted`, `InProgress`, or `FingerprintMismatch`.
  **This must be atomic**: two concurrent callers with the same key must not both receive
  `Acquired`.
- `Task CompleteAsync(string key, CachedResponse response, CancellationToken)` — store the response
  for a previously acquired key so later requests replay it.
- `Task ReleaseAsync(string key, CancellationToken)` — drop a still-in-progress claim that could not
  be completed, so the request can be retried.

`IdempotencyLease` exposes the `Outcome` (an `IdempotencyOutcome` enum) and, for
`AlreadyCompleted`, the `CachedResponse` to replay. `CachedResponse` carries the `StatusCode`
(required), `ContentType` (nullable), and `Body` (a `ReadOnlyMemory<byte>`, required).

### InMemoryIdempotencyStore

The default store is process-local and backed by a `Dictionary`:

- A single `lock` guards the claim/complete/release critical sections, providing the required
  atomicity on one instance.
- Entries carry an expiry derived from the configured TTL; expired entries are evicted lazily on
  access.
- `ReleaseAsync` only removes a still-in-progress entry; it never discards an already-cached
  response.

It is correct for a single instance or for tests. For a multi-instance deployment, implement
`IIdempotencyStore` over a shared backend (Redis, SQL, etc.) and register it before or after
`AddOrionOnce()`; the in-memory store is only added when no `IIdempotencyStore` is registered.

## Diagnostics

`IdempotencyDiagnostics` owns a `System.Diagnostics.Metrics.Meter` named `Moongazing.OrionOnce`
(`IdempotencyDiagnostics.MeterName`) and a single counter `oriononce.requests` (unit `{request}`).
Every request records exactly one outcome tag:

| Outcome tag | Meaning |
|-------------|---------|
| `acquired` | Key claimed; handler ran and the response was captured |
| `replayed` | Stored response replayed for a repeated key |
| `in_progress` | Duplicate rejected `409` while the first request was in flight |
| `mismatch` | Key reused with a different request, rejected `422` |
| `missing_key` | Guarded request without a key while `RequireKey = true`, rejected `400` |
| `bypassed` | Guarded request without a key while `RequireKey = false` |

The diagnostics object is registered as a singleton and disposes its meter on shutdown.

## Registration

`AddOrionOnce(this IServiceCollection, Action<IdempotencyOptions>?)`:

- Builds and validates the options, then registers them as a singleton.
- Registers `IdempotencyDiagnostics` as a singleton (via `TryAddSingleton`).
- Registers `InMemoryIdempotencyStore` as the `IIdempotencyStore` **only if one is not already
  registered** (via `TryAddSingleton`), so a custom store always wins.

`UseOrionOnce(this IApplicationBuilder)` adds `IdempotencyMiddleware` to the pipeline. Place it
after routing and before the endpoints whose retries you want to deduplicate.

## Targeting

The library multi-targets `net8.0`, `net9.0`, and `net10.0`, with nullable reference types enabled,
implicit usings, and `TreatWarningsAsErrors`.

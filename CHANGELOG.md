<!-- markdownlint-disable MD024 -->

# Changelog

All notable changes to OrionOnce are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.2.1] - 2026-06-20

### Performance

`RequestFingerprint.Compute` runs on every guarded request before the store is consulted, so its
per-call allocations were on the hot path. The digest is now produced without the intermediate
allocations the previous version made, while the wire format (SHA-256 over `METHOD\npath\n` + body,
returned as 64-char lowercase hex) is byte-for-byte unchanged.

- The method is upper-cased into a stack buffer instead of allocating a string via
  `ToUpperInvariant()`.
- The hashed message is assembled in a single `ArrayPool`-rented buffer rather than allocating an
  interpolated header string, a header `byte[]`, and a combined `byte[]` sized to the body.
- The hex digest is written directly as lowercase into a stack buffer, replacing the
  `Convert.ToHexString(...).ToLowerInvariant()` pair of intermediate strings.

Net effect: allocation per call drops to a constant 152 B (only the returned string), independent of
body size, versus 536 B for a 32-byte body and ~8.7 KB for an 8 KB body before. Measured throughput
improves roughly 21 to 24 percent across small and large bodies. No public API or observable behavior
changes; all existing tests pass unchanged.

## [0.2.0] - 2026-06-19

### Added

Idempotent result capture-and-replay outside the HTTP pipeline, plus retention housekeeping.

- `IdempotentExecutor`: runs an operation once under an idempotency key and captures its typed
  result through the existing `IIdempotencyStore`, so a later duplicate call with the same key and
  fingerprint replays the stored result instead of re-running. Concurrent duplicates resolve to one
  execution; an in-flight or fingerprint-mismatched key surfaces as `IdempotentExecutionException`;
  a failed operation releases the key so the call can be retried.
- `IIdempotentResultCodec<TResult>` with `DelegateResultCodec<TResult>`: the caller supplies result
  serialization, keeping the library free of any serializer dependency.
- `IIdempotencyStore.SweepAsync`: purges expired entries and reports how many were removed, with a
  no-op default for stores that expire entries themselves. `InMemoryIdempotencyStore` implements it
  so abandoned keys (acquired but never seen again) can be reclaimed rather than lingering until the
  next access.
- `InMemoryIdempotencyStore(TimeSpan, TimeProvider)`: a `TimeProvider`-based clock for expiry,
  alongside the existing system-clock constructor.

### Tests

29 new tests covering the executor (capture, replay, expiry re-run, in-flight and mismatch
outcomes, release-on-failure, one-execution-under-concurrency, argument validation), the codec, the
sweep (live versus expired, ttl boundary, idempotent repeat, default no-op), and `TimeProvider`
expiry.

## [0.1.0] - 2026-06-14

### Added

Initial release. HTTP idempotency for ASP.NET Core.

- `IdempotencyMiddleware` plus `UseOrionOnce()`: replays the stored response for a repeated
  `Idempotency-Key`, returns `409` for an in-flight duplicate, `422` for a key reused with a
  different body, and `413` for an over-limit body. Releases the key (no caching) on a handler
  exception or a `5xx` so the client can retry.
- `IIdempotencyStore` with an in-process `InMemoryIdempotencyStore` (TTL, atomic claim); swap in
  a shared store for multi-instance deployments.
- `RequestFingerprint`: SHA-256 over method, path, and body to detect key reuse.
- `IdempotencyOptions`: header name, retention, guarded methods, require-key, max body size.
- `IdempotencyDiagnostics`: `Moongazing.OrionOnce` meter with an outcome-tagged request counter.
- `AddOrionOnce()` DI extension.

### Tests

25 tests across the store, the fingerprint, the middleware (replay, conflict, mismatch,
bypass, required-key, handler failure, 5xx not cached, body limit), and registration.

[0.2.1]: https://github.com/tunahanaliozturk/OrionOnce/releases/tag/v0.2.1
[0.2.0]: https://github.com/tunahanaliozturk/OrionOnce/releases/tag/v0.2.0
[0.1.0]: https://github.com/tunahanaliozturk/OrionOnce/releases/tag/v0.1.0

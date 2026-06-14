<!-- markdownlint-disable MD024 -->

# Changelog

All notable changes to OrionOnce are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[0.1.0]: https://github.com/tunahanaliozturk/OrionOnce/releases/tag/v0.1.0

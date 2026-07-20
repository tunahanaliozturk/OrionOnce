<!-- markdownlint-disable MD024 -->

# Changelog

All notable changes to OrionOnce are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Security

Pinned `SQLitePCLRaw.bundle_e_sqlite3` to 2.1.12 in the EF Core test project to clear
[GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) (High), a vulnerability in
the bundled SQLite native library.

- The advisory reaches this repository through test code only. `Microsoft.EntityFrameworkCore.Sqlite`
  is referenced solely by `Moongazing.OrionOnce.EntityFrameworkCore.Tests`, which pulled
  `SQLitePCLRaw.lib.e_sqlite3` 2.1.6 (`net8.0`), 2.1.10 (`net9.0`) and 2.1.11 (`net10.0`)
  transitively via `Microsoft.Data.Sqlite` -> `SQLitePCLRaw.bundle_e_sqlite3`. Pinning the bundle
  lifts `core`, `lib.e_sqlite3` and `provider.e_sqlite3` to the patched 2.1.12 on every target
  framework.
- **No shipped or released version is affected.** The `OrionOnce.EntityFrameworkCore` package
  references `Microsoft.EntityFrameworkCore.Relational` only — it carries no SQLite provider and no
  `SQLitePCLRaw` dependency — and no other packable project references them either. Nothing that was
  ever published contained the vulnerable library.
- The project-level `NU1903` suppression was removed. It had been added when no patched release
  existed on the feed; now that 2.1.12 is published the audit passes on its own, and keeping the
  suppression would have hidden any future advisory in this dependency.

## [0.3.0] - 2026-06-22

### Added

A durable EF Core idempotency store in a new package, so idempotency holds across process restarts
and across instances sharing one database.

- `OrionOnce.EntityFrameworkCore` (namespace `Moongazing.OrionOnce.EntityFrameworkCore`): an
  `EntityFrameworkCoreIdempotencyStore<TContext>` implementing `IIdempotencyStore` over EF Core. It
  persists each key, its in-flight lease, and the captured response, and mirrors the in-memory store
  exactly: an entry is live only while its retention window has not elapsed, an expired entry is
  reclaimable, completing refreshes the window, and a release drops only a still-in-flight claim and
  never discards a stored response.
- `AcquireAsync` is atomic without a read-then-write race. The key is the table's primary key, so the
  first caller's insert wins the lease and a concurrent second insert for the same key is rejected by
  the database's unique constraint. The resulting `DbUpdateException` is treated as "already in
  flight or completed" only after re-reading the row to confirm a real collision, rather than by
  inspecting a provider-specific SQL error code, so the package stays provider-agnostic and a
  genuinely different failure (for example a missing table) surfaces instead of being swallowed.
- `SweepAsync` bulk-deletes expired rows with `ExecuteDeleteAsync`, served by an index on the expiry
  column, without loading rows or touching the change tracker.
- `OrionOnceDbContext` and `IdempotencyEntryConfiguration` for either a dedicated context or folding
  the table into an existing one, and `AddOrionOnceEntityFrameworkCoreStore(...)` to register the
  store (and its `IDbContextFactory`) as the application's `IIdempotencyStore`.
- The package references `Microsoft.EntityFrameworkCore.Relational` only and pins one EF Core major
  per target framework (8.0.x on `net8.0`, 9.0.x on `net9.0`, 10.0.x on `net10.0`), so the consuming
  application chooses the database provider.

### Changed

- The `IdempotencyLease` factory members (`Acquired`, `InProgress`, `FingerprintMismatch`, and
  `Completed(response)`) are now `public` rather than internal, so an out-of-box `IIdempotencyStore`
  implementation in another assembly can return them. This widens accessibility only; no signature
  or behavior changes.

### Tests

A reusable `IdempotencyStoreConformanceTests` base exercises the `IIdempotencyStore` contract (first
acquire wins, in-flight replay/conflict, response capture-and-replay, fingerprint mismatch, release
semantics, expiry, and sweep) against a store factory, and the EF Core store is run through it over a
real file-backed SQLite database (genuine constraints and transactions, not EF's in-memory provider).
A dedicated concurrency test fires up to 64 parallel `AcquireAsync` calls at one key behind a barrier
and asserts exactly one winner with the rest reported in flight, and a failure test confirms a
non-duplicate `DbUpdateException` is surfaced rather than mistaken for a conflict. 13 new tests across
`net8.0`, `net9.0`, and `net10.0`.

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

[0.3.0]: https://github.com/tunahanaliozturk/OrionOnce/releases/tag/v0.3.0
[0.2.1]: https://github.com/tunahanaliozturk/OrionOnce/releases/tag/v0.2.1
[0.2.0]: https://github.com/tunahanaliozturk/OrionOnce/releases/tag/v0.2.0
[0.1.0]: https://github.com/tunahanaliozturk/OrionOnce/releases/tag/v0.1.0

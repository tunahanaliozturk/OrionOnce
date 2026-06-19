<p align="center">
  <img src="docs/logo.png" alt="OrionOnce" width="150" />
</p>

# OrionOnce

[![CI/CD](https://github.com/tunahanaliozturk/OrionOnce/actions/workflows/ci-cd.yml/badge.svg)](https://github.com/tunahanaliozturk/OrionOnce/actions/workflows/ci-cd.yml)
[![NuGet](https://img.shields.io/nuget/v/OrionOnce.svg)](https://www.nuget.org/packages/OrionOnce/)

HTTP idempotency for ASP.NET Core. A client sends an `Idempotency-Key` with a request; if the
same key arrives again, OrionOnce replays the first response instead of running your handler a
second time. Retries stop double-charging, double-shipping, and double-posting.

Part of the **Orion** family. Usable entirely on its own.

## Why

Networks drop responses, clients retry, and load balancers replay. Without idempotency a retried
`POST /payments` charges twice. The fix is well understood (key the request, cache the response,
replay on repeat) but fiddly to get right: you have to buffer the body, detect a key reused for a
different request, reject duplicates that are still in flight, and avoid caching transient
failures. OrionOnce does those four things.

## Features

- **`Idempotency-Key` middleware** that runs your handler once per key and replays the captured
  response on every retry, marked with an `Idempotency-Replayed: true` header.
- **Request fingerprinting** via SHA-256 over method, path (with query), and body, so a key reused
  for a different request is detected and rejected instead of silently replayed.
- **In-flight protection**: a duplicate that arrives while the first request is still running gets
  `409 Conflict` rather than a second execution.
- **No caching of transient failures**: a handler exception or a `5xx` response releases the key,
  so the client can safely retry it.
- **Body-size guard**: bodies larger than `MaxBodyBytes` are rejected with `413` before any work.
- **Pluggable storage** behind `IIdempotencyStore`; ships with an in-process
  `InMemoryIdempotencyStore` and lets you swap in a shared store for multi-instance deployments.
- **Capture-and-replay outside HTTP** via `IdempotentExecutor`: run an operation once per key and
  replay its captured typed result on later duplicate calls, over the same `IIdempotencyStore`.
- **Retention housekeeping** via `IIdempotencyStore.SweepAsync`, which purges expired entries that
  were acquired but never seen again, plus a `TimeProvider`-based clock for testable expiry.
- **OpenTelemetry metrics** through a `Moongazing.OrionOnce` meter with an outcome-tagged counter.
- **Multi-targeted** for `net8.0`, `net9.0`, and `net10.0`, nullable-enabled, warnings-as-errors.

## Install

```
dotnet add package OrionOnce
```

The package id is `OrionOnce`; the root namespace is `Moongazing.OrionOnce`.

## Quick start

Register the services, then add the middleware after routing and before your endpoints.

```csharp
using Moongazing.OrionOnce;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOrionOnce(o =>
{
    o.Retention = TimeSpan.FromHours(24);
    o.RequireKey = false;                 // set true to make the key mandatory on guarded methods
});

var app = builder.Build();

app.UseRouting();
app.UseOrionOnce();                        // after routing, before your endpoints
app.MapControllers();

app.Run();
```

A client then retries safely:

```
POST /payments
Idempotency-Key: 3f1c9b8a-...

# first call  -> handler runs, 201 created, response cached
# retry       -> handler skipped, the same 201 replayed with "Idempotency-Replayed: true"
```

Only `POST`, `PUT`, `PATCH`, and `DELETE` are guarded by default; safe methods (`GET`, `HEAD`,
`OPTIONS`) pass through untouched because they need no protection.

## Usage

### Conflict and replay behaviour

The middleware resolves every guarded request carrying a key to exactly one of these outcomes:

| Situation | Result |
|-----------|--------|
| Key not seen before | Handler runs once; its response is cached |
| Same key, same request, already completed | Stored response replayed, `Idempotency-Replayed: true` |
| Same key, request still in flight | `409 Conflict` (no second execution) |
| Same key, different request body | `422 Unprocessable Entity` |
| Guarded method, no key, `RequireKey = true` | `400 Bad Request` |
| Guarded method, no key, `RequireKey = false` | Bypassed; handled normally |
| Handler throws or returns `5xx` | Key released, not cached, so the client can retry |
| Body larger than `MaxBodyBytes` | `413 Payload Too Large` |

### Fingerprint scope

A key alone does not identify a request. OrionOnce binds each key to a fingerprint computed by
`RequestFingerprint.Compute`, a SHA-256 over the uppercased method, the path plus query string, and
the body bytes. Two requests that present the same key must produce the same fingerprint; otherwise
the second is rejected with `422`. The fingerprint is exposed as a static helper if you need to
compute it yourself:

```csharp
using Moongazing.OrionOnce;

string fingerprint = RequestFingerprint.Compute("POST", "/orders?expedite=1", bodyBytes);
// 64-character lowercase hex SHA-256 digest
```

### Custom store

The default store is process-local. For a deployment with more than one instance, implement
`IIdempotencyStore` over a shared backend (Redis, SQL, etc.) and register it before or after
`AddOrionOnce()`. The library only adds the in-memory store when none is present, so your
registration wins.

```csharp
using Moongazing.OrionOnce.Storage;

public sealed class RedisIdempotencyStore : IIdempotencyStore
{
    public Task<IdempotencyLease> AcquireAsync(
        string key, string fingerprint, CancellationToken cancellationToken = default)
    {
        // Must be atomic: two concurrent requests with the same key must not both
        // receive IdempotencyOutcome.Acquired. Return:
        //   IdempotencyLease.Completed(response) when the key already finished,
        //   the in-progress outcome while another request holds the key,
        //   the mismatch outcome when the stored fingerprint differs,
        //   the acquired outcome when this caller claims the key.
        throw new NotImplementedException();
    }

    public Task CompleteAsync(
        string key, CachedResponse response, CancellationToken cancellationToken = default) =>
        // Persist the captured response so later requests replay it.
        throw new NotImplementedException();

    public Task ReleaseAsync(string key, CancellationToken cancellationToken = default) =>
        // Drop a still-in-progress claim so the request can be retried.
        throw new NotImplementedException();
}
```

```csharp
builder.Services.AddSingleton<IIdempotencyStore>(new RedisIdempotencyStore(/* ... */));
builder.Services.AddOrionOnce();
```

`AcquireAsync` must be atomic so two concurrent requests with the same key cannot both be told to
proceed. See [docs/FEATURES.md](docs/FEATURES.md) for the full store contract and lifecycle.

### Idempotent execution outside HTTP

Not every duplicate arrives as an HTTP request. A retried message-queue delivery, a re-invoked
background job, or a repeated RPC needs the same guarantee: run the operation once, replay its
result the next time. `IdempotentExecutor` provides that over the same `IIdempotencyStore`. The
first call runs the operation and captures its typed result; a later call with the same key and
fingerprint replays the stored result without running the operation again.

The library ships no serializer, so you supply a codec that turns the result into bytes and back.
`DelegateResultCodec<TResult>` wraps a serialize and a deserialize delegate inline, here with
`System.Text.Json`:

```csharp
using System.Text.Json;
using Moongazing.OrionOnce;
using Moongazing.OrionOnce.Storage;

var store = new InMemoryIdempotencyStore(TimeSpan.FromHours(24));
var executor = new IdempotentExecutor(store);

var codec = new DelegateResultCodec<Receipt>(
    serialize: receipt => JsonSerializer.SerializeToUtf8Bytes(receipt),
    deserialize: payload => JsonSerializer.Deserialize<Receipt>(payload)!,
    contentType: "application/json");

string key = message.IdempotencyKey;
string fingerprint = RequestFingerprint.Compute("charge", message.OrderId, message.Body);

Receipt receipt = await executor.ExecuteAsync(
    key,
    fingerprint,
    ct => ChargeAsync(message, ct),   // runs at most once per key
    codec,
    cancellationToken);
```

The outcomes mirror the middleware. A completed key replays its stored result. A key still held by
a concurrent caller, or reused with a different fingerprint, throws `IdempotentExecutionException`
carrying the `IdempotencyOutcome` (`InProgress` or `FingerprintMismatch`) so you can map it to a
retry or a rejection. If the operation throws, or its result cannot be captured, the key is released
so the call can be retried, and the original exception propagates unchanged.

### Reclaiming expired entries

`InMemoryIdempotencyStore` evicts expired entries lazily on access, so a key that is acquired and
never touched again holds its memory until the retention window is swept. `SweepAsync` removes every
entry whose window has elapsed and returns how many it removed; run it periodically (for example
from a `BackgroundService`) to reclaim that memory:

```csharp
int removed = await store.SweepAsync(cancellationToken);
```

The default interface implementation is a no-op returning zero, which suits stores such as Redis
that expire entries themselves. For testable expiry, the in-memory store also accepts a
`TimeProvider`, so a fake clock can drive entries past their window in a unit test:

```csharp
var store = new InMemoryIdempotencyStore(TimeSpan.FromMinutes(5), timeProvider);
```

## Configuration

`AddOrionOnce` takes an optional `Action<IdempotencyOptions>`. The options are validated at
registration (`HeaderName` must be non-empty; `Retention` and `MaxBodyBytes` must be positive).

| Option | Type | Default | Purpose |
|--------|------|---------|---------|
| `HeaderName` | `string` | `Idempotency-Key` | The request header that carries the key |
| `Retention` | `TimeSpan` | 24 hours | How long a captured response is retained for replay |
| `Methods` | `ISet<string>` | `POST`, `PUT`, `PATCH`, `DELETE` | The HTTP methods that are guarded (case-insensitive) |
| `RequireKey` | `bool` | `false` | When `true`, a guarded request with no key is rejected `400`; when `false`, it bypasses idempotency |
| `MaxBodyBytes` | `int` | 1 MiB | Largest buffered request body; larger bodies are rejected `413` |

```csharp
builder.Services.AddOrionOnce(o =>
{
    o.HeaderName = "Idempotency-Key";
    o.Retention = TimeSpan.FromHours(6);
    o.RequireKey = true;
    o.MaxBodyBytes = 256 * 1024;
    o.Methods.Add("POST");                 // Methods is a mutable set; adjust to taste
});
```

## Telemetry

OrionOnce publishes metrics through `IdempotencyDiagnostics`, which owns a `Meter` named
`Moongazing.OrionOnce` (also exposed as `IdempotencyDiagnostics.MeterName`). It defines a single
counter, `oriononce.requests`, tagged with `outcome`:

`acquired`, `replayed`, `in_progress`, `mismatch`, `missing_key`, `bypassed`.

Subscribe to the meter from OpenTelemetry:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter(IdempotencyDiagnostics.MeterName));
```

The replayed response also carries the `Idempotency-Replayed: true` header
(`IdempotencyMiddleware.ReplayedHeader`), which clients and proxies can inspect directly.

## Testing

The library is covered by an xUnit suite under `tests/Moongazing.OrionOnce.Tests` spanning the
store, the fingerprint, the middleware (replay, conflict, mismatch, bypass, required-key, handler
failure, `5xx` not cached, body limit), and registration.

```
dotnet test
```

Benchmarks for the in-process hot paths (fingerprint, store, and combined request flow) live under
`benchmarks/Moongazing.OrionOnce.Benchmarks` and are documented in
[benchmarks.md](benchmarks.md). No measured numbers are committed; run the suite on the hardware
you care about:

```
dotnet run -c Release --project benchmarks/Moongazing.OrionOnce.Benchmarks
```

## Versioning

OrionOnce follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Notable changes are
recorded in [CHANGELOG.md](CHANGELOG.md). The current release is `0.2.0`; while the major version
is `0`, the public surface may still change between minor versions.

## Design notes

- Multi-targets `net8.0`, `net9.0`, `net10.0`.
- `TreatWarningsAsErrors`, latest analyzers, nullable enabled.
- Response capture replays the status, content type, and body. Other response headers are not
  replayed in this version.

See [docs/FEATURES.md](docs/FEATURES.md) for a deeper breakdown and [docs/ROADMAP.md](docs/ROADMAP.md)
for ideas under consideration.

## Contributing

Contributions are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) and the
[CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) before opening a pull request.

## License

[MIT](LICENSE).

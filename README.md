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

## Install

```
dotnet add package OrionOnce
```

## Quick start

```csharp
builder.Services.AddOrionOnce(o =>
{
    o.Retention = TimeSpan.FromHours(24);
    o.RequireKey = false;                 // set true to make the key mandatory on guarded methods
});

var app = builder.Build();
app.UseRouting();
app.UseOrionOnce();                        // after routing, before your endpoints
app.MapControllers();
```

A client then retries safely:

```
POST /payments
Idempotency-Key: 3f1c9b8a-...

# first call  -> handler runs, 201 created, response cached
# retry       -> handler skipped, the same 201 replayed with "Idempotency-Replayed: true"
```

## Behaviour

| Situation | Result |
|-----------|--------|
| Key not seen before | Handler runs once; its response is cached |
| Same key, same request, already completed | Stored response replayed, `Idempotency-Replayed: true` |
| Same key, request still in flight | `409 Conflict` (no second execution) |
| Same key, different request body | `422 Unprocessable Entity` |
| Guarded method, no key, `RequireKey = true` | `400 Bad Request` |
| Handler throws or returns `5xx` | Key released, not cached, so the client can retry |
| Body larger than `MaxBodyBytes` | `413 Payload Too Large` |

Only `POST`, `PUT`, `PATCH`, and `DELETE` are guarded by default (configurable via `Methods`);
safe methods need no protection.

## Storage

The default store is an in-process `InMemoryIdempotencyStore`, which is correct for a single
instance. For a multi-instance deployment, implement `IIdempotencyStore` over Redis or a database
and register it before `AddOrionOnce()`; the in-memory store is only added if none is present.
Implementations must make `AcquireAsync` atomic so two concurrent requests with the same key
cannot both be told to proceed.

## Telemetry

Subscribe to the `Moongazing.OrionOnce` meter. The `oriononce.requests` counter is tagged with
`outcome`: `acquired`, `replayed`, `in_progress`, `mismatch`, `missing_key`, or `bypassed`.

## Design

- Multi-targets `net8.0`, `net9.0`, `net10.0`.
- `TreatWarningsAsErrors`, latest analyzers, nullable enabled.
- Response capture replays the status, content type, and body. Other response headers are not
  replayed in this version.

## License

MIT.

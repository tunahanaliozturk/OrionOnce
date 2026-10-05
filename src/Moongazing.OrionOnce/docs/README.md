# OrionOnce

HTTP idempotency for ASP.NET Core: a client sends an `Idempotency-Key`, and a retry with the same key gets the first response replayed instead of running your handler a second time. `IdempotentExecutor` gives the same guarantee to queue consumers and jobs.

![How the OrionOnce middleware handles a request: method, key and body-size gates, an atomic AcquireAsync claim, then replay, 409, 422 or running the handler once; a thrown handler or a 5xx releases the key](https://raw.githubusercontent.com/tunahanaliozturk/OrionOnce/main/docs/diagrams/middleware-flow.png)

## Install

    dotnet add package OrionOnce

## Quick start

```csharp
using Moongazing.OrionOnce;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOrionOnce(o =>
{
    o.Retention = TimeSpan.FromHours(24);
    o.RequireKey = false;                 // true: a guarded request without a key gets 400
});

var app = builder.Build();

app.UseRouting();
app.UseOrionOnce();                        // after routing, before your endpoints
app.MapControllers();

app.Run();
```

A client retries `POST /payments` with `Idempotency-Key: 3f1c...`: the first call runs the handler and caches its response; every retry gets the same status, content type and body back with `Idempotency-Replayed: true`.

## Outcomes

| Situation | Result |
|-----------|--------|
| Key not seen before | Handler runs once; its response is cached |
| Same key and request, already completed | Stored response replayed, `Idempotency-Replayed: true` |
| Same key, request still in flight | `409 Conflict` |
| Same key, different method, path, query or body | `422 Unprocessable Entity` |
| Guarded method, no key, `RequireKey = true` | `400 Bad Request` |
| Guarded method, no key, `RequireKey = false` | Bypassed, handled normally |
| Handler throws or returns `5xx` | Key released, nothing cached, the client can retry |
| Body larger than `MaxBodyBytes` | `413 Payload Too Large` |

Other response headers are not captured or replayed.

## Options

`IdempotencyOptions`, validated inside `AddOrionOnce` (an invalid value throws there):

- `HeaderName` - the request header carrying the key; must not be empty. Default `Idempotency-Key`.
- `Retention` - how long a captured response is kept for replay; positive. Default 24 hours.
- `Methods` - guarded HTTP methods, case-insensitive and mutable. Default `POST`, `PUT`, `PATCH`, `DELETE`.
- `RequireKey` - reject a guarded request without a key with `400`. Default `false`.
- `MaxBodyBytes` - largest buffered request body; positive. Default 1 MiB.

## Outside HTTP

```csharp
using System.Text.Json;
using Moongazing.OrionOnce;
using Moongazing.OrionOnce.Storage;

var executor = new IdempotentExecutor(store);   // any IIdempotencyStore
var codec = new DelegateResultCodec<Receipt>(
    serialize: receipt => JsonSerializer.SerializeToUtf8Bytes(receipt),
    deserialize: payload => JsonSerializer.Deserialize<Receipt>(payload)!,
    contentType: "application/json");

string fingerprint = RequestFingerprint.Compute("charge", message.OrderId, message.Body);
Receipt receipt = await executor.ExecuteAsync(
    message.IdempotencyKey, fingerprint, ct => ChargeAsync(message, ct), codec, cancellationToken);
```

A completed key replays the stored result. A key still in flight or reused with a different fingerprint throws `IdempotentExecutionException` with its `Outcome`. A failed operation releases the key and its exception propagates unchanged.

## Storage and telemetry

- The default `InMemoryIdempotencyStore` is process-local. For more than one instance, install `OrionOnce.EntityFrameworkCore` or register your own `IIdempotencyStore`; `AddOrionOnce` adds the in-memory store only when none is registered. `AcquireAsync` must be atomic.
- `IIdempotencyStore.SweepAsync` removes expired entries; run it periodically against the in-memory store, which otherwise evicts only on access.
- Meter `Moongazing.OrionOnce` (`IdempotencyDiagnostics.MeterName`): counter `orion.once.requests`, tag `orion.outcome` = `acquired`, `replayed`, `in_progress`, `mismatch`, `missing_key` or `bypassed`. Built on `OrionInstrumentation` from `Orion.Abstractions`.
- Targets net8.0, net9.0 and net10.0. A NativeAOT publish of the in-memory store is smoke-tested in CI.

## Related packages

- `OrionOnce.EntityFrameworkCore` - durable `IIdempotencyStore` over EF Core, shared across instances.

## Links

- Documentation and full README: https://github.com/tunahanaliozturk/OrionOnce
- Changelog: https://github.com/tunahanaliozturk/OrionOnce/blob/main/CHANGELOG.md
- License: MIT

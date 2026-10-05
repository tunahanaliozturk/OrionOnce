# OrionOnce.EntityFrameworkCore

An Entity Framework Core `IIdempotencyStore` for OrionOnce, so idempotency keys and captured responses survive a restart and a retry that lands on another instance still replays the first response.

![OrionOnce overview: OrionOnce.EntityFrameworkCore implements IIdempotencyStore for the core and persists entries to a relational database](https://raw.githubusercontent.com/tunahanaliozturk/OrionOnce/main/docs/diagrams/overview.png)

## Install

    dotnet add package OrionOnce.EntityFrameworkCore

It plugs into `OrionOnce` and depends only on `Microsoft.EntityFrameworkCore.Relational`. Add the EF Core provider for your database as well, for example `Npgsql.EntityFrameworkCore.PostgreSQL` or `Microsoft.EntityFrameworkCore.SqlServer`.

## Quick start

```csharp
using Microsoft.EntityFrameworkCore;
using Moongazing.OrionOnce;
using Moongazing.OrionOnce.EntityFrameworkCore;

builder.Services.AddOrionOnce(o => o.Retention = TimeSpan.FromHours(24));

// Registers IDbContextFactory<OrionOnceDbContext> and the EF Core store as the IIdempotencyStore.
builder.Services.AddOrionOnceEntityFrameworkCoreStore(
    o => o.UseNpgsql(builder.Configuration.GetConnectionString("OrionOnce")));
```

The store replaces the in-memory default whichever order the two calls run in. Its retention is the optional `retention` argument, otherwise `IdempotencyOptions.Retention`. It uses the registered `TimeProvider` when there is one.

## Using your own DbContext

```csharp
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new IdempotencyEntryConfiguration());
    }
}

builder.Services.AddOrionOnceEntityFrameworkCoreStore<AppDbContext>(
    o => o.UseNpgsql(connectionString));
```

`IdempotencyEntryConfiguration` maps to the table `OrionOnceIdempotencyEntries` by default; pass a table name to its constructor to change it. The store does not create the schema: add a migration and apply it in your deployment.

## Behaviour

- `AcquireAsync` is atomic through the primary key on `Key`: the first insert wins. A concurrent insert of the same key fails, the row is re-read, and the caller gets `InProgress`, `AlreadyCompleted` or `FingerprintMismatch`. A failure that is not a duplicate key (for example a missing table) is surfaced, not reported as a conflict.
- An expired row is reclaimed in place by the next `AcquireAsync`. `CompleteAsync` stores the response and restarts the retention window.
- `ReleaseAsync` deletes a row only while it is still in flight; a stored response is never discarded.
- `SweepAsync` bulk-deletes expired rows with `ExecuteDeleteAsync`, served by the index on `ExpiresAtTicks`. Run it periodically.
- Each call creates a short-lived context from the factory, so the singleton store is safe under concurrent requests.

Targets net8.0, net9.0 and net10.0 with the matching EF Core major per target (`8.0.x`, `9.0.x`, `10.0.x`).

## Related packages

- `OrionOnce` - the idempotency middleware and `IdempotentExecutor` this store plugs into.

## Links

- Documentation and full README: https://github.com/tunahanaliozturk/OrionOnce
- Changelog: https://github.com/tunahanaliozturk/OrionOnce/blob/main/CHANGELOG.md
- License: MIT

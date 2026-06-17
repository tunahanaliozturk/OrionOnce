# Benchmarks

Microbenchmarks for OrionOnce's in-process hot paths, built with
[BenchmarkDotNet](https://benchmarkdotnet.org/). They cover the request fingerprint and the
in-memory store the library ships by default. No database, Redis, or network is involved, so the
numbers are stable and reproducible on a single machine.

The project lives in `benchmarks/Moongazing.OrionOnce.Benchmarks` and references the library
directly. Every class is a `[MemoryDiagnoser]` and runs on .NET 8 and .NET 9.

## What is measured

### RequestFingerprintBenchmarks
`RequestFingerprint.Compute` is the SHA-256 over method, path, and body that binds an idempotency
key to a specific request. It runs on every guarded request before the store is consulted, and its
cost scales with body size. Parameterized over body sizes of 64 B, 4 KiB, and 256 KiB.

### AcquireBenchmarks
The three steady-state outcomes of `InMemoryIdempotencyStore.AcquireAsync` against a pre-populated
store: a fresh key (claimed), a completed key (replayed), and a key reused for a different body
(mismatch). These are the branches the middleware takes on every retried request. `AcquireFresh`
is the baseline.

### LifecycleBenchmarks
Whole key lifecycles against the store, each iteration using a unique key: acquire then complete
(the once-only success path) and acquire then release (the handler-failed retry path). This
exercises the dictionary insert, update, and removal the real flow performs rather than re-reading
a warm entry.

### RequestFlowBenchmarks
The two combined paths the middleware drives per guarded request: fingerprint then claim a fresh
key (first call), and fingerprint then resolve a completed key (retry that replays). Pairs the
SHA-256 cost with the store lookup the way production does, showing the per-request floor.

## Running

Run all benchmarks (Release is required):

```
dotnet run -c Release --project benchmarks/Moongazing.OrionOnce.Benchmarks
```

Filter to one class or method:

```
dotnet run -c Release --project benchmarks/Moongazing.OrionOnce.Benchmarks -- --filter "*RequestFingerprintBenchmarks*"
dotnet run -c Release --project benchmarks/Moongazing.OrionOnce.Benchmarks -- --filter "*AcquireReplayed*"
```

List the available benchmarks without running them:

```
dotnet run -c Release --project benchmarks/Moongazing.OrionOnce.Benchmarks -- --list flat
```

BenchmarkDotNet writes full results (including markdown tables) under
`BenchmarkDotNet.Artifacts/results` after a run.

## Notes

- The benchmark host targets `net10.0`; the measured jobs target `net8.0` and `net9.0` via
  `[SimpleJob]`, so both runtimes must be installed for a full run.
- No measured numbers are committed here on purpose. Results depend on hardware, OS, and runtime
  version, so run the suite on the machine you care about and read the generated tables.

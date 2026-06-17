# OrionOnce roadmap

Ideas under consideration, not commitments. There are no dates here. Anything listed may change,
ship differently, or be dropped. Today the library does exactly what the
[README](../README.md) and [FEATURES](FEATURES.md) describe; everything below is beyond that.

## Storage

- A shipped shared-store implementation (for example Redis) so multi-instance deployments do not
  have to write their own `IIdempotencyStore`.
- Optional persistence guidance or helpers for distributed stores, including how to keep
  `AcquireAsync` atomic across nodes.

## Response fidelity

- Capturing and replaying a configurable set of response headers, not just status, content type,
  and body.
- A hook to let applications decide which responses are cacheable beyond the current
  "anything below `5xx`" rule.

## Fingerprinting

- Configurable fingerprint scope (for example, including or excluding the query string, or selected
  headers) for applications whose notion of "the same request" differs from the default.

## Observability

- Additional metrics or tags (such as latency or per-route breakdowns) and optional tracing spans
  alongside the existing outcome counter.

## Ergonomics

- Per-endpoint or attribute-based opt-in/opt-out, so idempotency can be scoped more finely than the
  method set.
- Sample projects showing OrionOnce alongside a real shared store.

If one of these matters to you, open an issue describing the use case. Concrete needs shape what
gets built.

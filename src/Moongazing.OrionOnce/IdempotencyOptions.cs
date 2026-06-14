namespace Moongazing.OrionOnce;

/// <summary>
/// Configuration for the idempotency middleware: which header carries the key, how long results
/// are retained, which methods are guarded, and whether a key is mandatory.
/// </summary>
public sealed class IdempotencyOptions
{
    /// <summary>The request header carrying the idempotency key. Default <c>Idempotency-Key</c>.</summary>
    public string HeaderName { get; set; } = "Idempotency-Key";

    /// <summary>How long a captured response is retained for replay. Default 24 hours.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// The HTTP methods the middleware guards. Idempotent methods (GET, HEAD, OPTIONS) are not
    /// included by default because they need no protection. Compared case-insensitively.
    /// </summary>
    public ISet<string> Methods { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };

    /// <summary>
    /// When true, a guarded request that omits the key is rejected with 400. When false (the
    /// default), a request without a key bypasses idempotency and is handled normally.
    /// </summary>
    public bool RequireKey { get; set; }

    /// <summary>
    /// The largest request body, in bytes, that is buffered to compute the fingerprint and to
    /// allow the handler to re-read. Bodies larger than this are rejected with 413. Default 1 MiB.
    /// </summary>
    public int MaxBodyBytes { get; set; } = 1024 * 1024;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrEmpty(HeaderName);
        if (Retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Retention), Retention, "Retention must be positive.");
        }
        if (MaxBodyBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBodyBytes), MaxBodyBytes, "MaxBodyBytes must be positive.");
        }
    }
}

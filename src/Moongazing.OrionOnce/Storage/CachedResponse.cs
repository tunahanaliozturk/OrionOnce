namespace Moongazing.OrionOnce.Storage;

/// <summary>
/// The captured result of a successfully processed request, replayed verbatim when the same
/// idempotency key is seen again. Captures the status line, content type, and body; response
/// headers beyond content type are intentionally not replayed in this version.
/// </summary>
public sealed class CachedResponse
{
    /// <summary>The HTTP status code that was produced.</summary>
    public required int StatusCode { get; init; }

    /// <summary>The response content type, or null when the response had no body.</summary>
    public string? ContentType { get; init; }

    /// <summary>The response body bytes, empty when there was no body.</summary>
    public required ReadOnlyMemory<byte> Body { get; init; }
}

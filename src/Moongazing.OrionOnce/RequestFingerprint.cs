namespace Moongazing.OrionOnce;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Computes the request fingerprint bound to an idempotency key. Two requests carrying the same
/// key must have the same fingerprint; a mismatch means the key was reused for a different
/// request and is rejected. The fingerprint is a SHA-256 over the method, path, and body.
/// </summary>
public static class RequestFingerprint
{
    /// <summary>Compute the lowercase hex fingerprint of a request.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">The request path (and query) as routed.</param>
    /// <param name="body">The request body bytes.</param>
    /// <returns>A 64-character lowercase hex SHA-256 digest.</returns>
    public static string Compute(string method, string path, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(path);

        var header = Encoding.UTF8.GetBytes($"{method.ToUpperInvariant()}\n{path}\n");
        var buffer = new byte[header.Length + body.Length];
        header.CopyTo(buffer.AsSpan());
        body.CopyTo(buffer.AsSpan(header.Length));

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(buffer, digest);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}

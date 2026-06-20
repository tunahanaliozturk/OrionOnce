namespace Moongazing.OrionOnce;

using System.Buffers;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Computes the request fingerprint bound to an idempotency key. Two requests carrying the same
/// key must have the same fingerprint; a mismatch means the key was reused for a different
/// request and is rejected. The fingerprint is a SHA-256 over the method, path, and body.
/// </summary>
public static class RequestFingerprint
{
    // UTF-8 bytes of an ASCII character never exceed one byte per char; method and path are encoded
    // with one separator byte ('\n') after each. The largest stack buffer we are willing to take for
    // the method-upper conversion before renting from the pool.
    private const int MaxMethodStackChars = 64;

    // The hashed message is "METHOD\npath\n" + body. Anything up to this size is assembled on the
    // stack; larger messages are assembled in a pooled buffer to avoid a per-call allocation.
    private const int MaxMessageStackBytes = 256;

    /// <summary>Compute the lowercase hex fingerprint of a request.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">The request path (and query) as routed.</param>
    /// <param name="body">The request body bytes.</param>
    /// <returns>A 64-character lowercase hex SHA-256 digest.</returns>
    public static string Compute(string method, string path, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(path);

        // Upper-case the method using invariant culture, matching string.ToUpperInvariant exactly,
        // without allocating the intermediate string.
        char[]? rentedMethod = null;
        Span<char> upperMethod = method.Length <= MaxMethodStackChars
            ? stackalloc char[MaxMethodStackChars]
            : (rentedMethod = ArrayPool<char>.Shared.Rent(method.Length));
        upperMethod = upperMethod[..method.Length];
        method.AsSpan().ToUpperInvariant(upperMethod);

        try
        {
            // Worst-case UTF-8 size of "METHOD" + "\n" + "path" + "\n", before the body is appended.
            var prefixMaxBytes = Encoding.UTF8.GetMaxByteCount(upperMethod.Length)
                + 1
                + Encoding.UTF8.GetMaxByteCount(path.Length)
                + 1;

            // Assemble the prefix once so we know its exact byte length, then size the full message
            // buffer as prefix + body and hash it in a single pass.
            byte[]? rentedPrefix = null;
            Span<byte> prefix = prefixMaxBytes <= MaxMessageStackBytes
                ? stackalloc byte[MaxMessageStackBytes]
                : (rentedPrefix = ArrayPool<byte>.Shared.Rent(prefixMaxBytes));

            try
            {
                var written = Encoding.UTF8.GetBytes(upperMethod, prefix);
                prefix[written++] = (byte)'\n';
                written += Encoding.UTF8.GetBytes(path.AsSpan(), prefix[written..]);
                prefix[written++] = (byte)'\n';

                return HashMessage(prefix[..written], body);
            }
            finally
            {
                if (rentedPrefix is not null)
                {
                    // The prefix holds "METHOD\npath\n", which includes the routed path and query.
                    // Clear it so request data is not left in the shared pool for the next renter.
                    ArrayPool<byte>.Shared.Return(rentedPrefix, clearArray: true);
                }
            }
        }
        finally
        {
            if (rentedMethod is not null)
            {
                ArrayPool<char>.Shared.Return(rentedMethod);
            }
        }
    }

    private static string HashMessage(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> body)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];

        var messageLength = prefix.Length + body.Length;
        byte[]? rentedMessage = null;
        Span<byte> message = messageLength <= MaxMessageStackBytes
            ? stackalloc byte[MaxMessageStackBytes]
            : (rentedMessage = ArrayPool<byte>.Shared.Rent(messageLength));

        try
        {
            prefix.CopyTo(message);
            body.CopyTo(message[prefix.Length..]);
            SHA256.HashData(message[..messageLength], digest);
        }
        finally
        {
            if (rentedMessage is not null)
            {
                // The message holds the prefix plus the full request body. Clear it so the
                // plaintext payload is not left in the shared pool for the next renter to observe.
                ArrayPool<byte>.Shared.Return(rentedMessage, clearArray: true);
            }
        }

        return ToLowerHex(digest);
    }

    // Encode the digest as lowercase hex into a stack buffer and materialise the string once,
    // avoiding the Convert.ToHexString (uppercase) plus ToLowerInvariant pair of intermediate
    // string allocations. Only the returned 64-char string is allocated.
    private static string ToLowerHex(ReadOnlySpan<byte> digest)
    {
        ReadOnlySpan<char> hex = "0123456789abcdef";
        Span<char> chars = stackalloc char[digest.Length * 2];
        for (var i = 0; i < digest.Length; i++)
        {
            var b = digest[i];
            chars[i * 2] = hex[b >> 4];
            chars[(i * 2) + 1] = hex[b & 0xF];
        }

        return new string(chars);
    }
}

namespace Moongazing.OrionOnce;

/// <summary>
/// Converts an operation result to and from the bytes stored for replay by
/// <see cref="IdempotentExecutor"/>. The library ships no serializer, so the caller chooses one
/// (System.Text.Json, Protobuf, a hand-rolled format) by implementing this interface or wrapping
/// delegates with <see cref="DelegateResultCodec{TResult}"/>.
/// </summary>
/// <typeparam name="TResult">The operation result type.</typeparam>
public interface IIdempotentResultCodec<TResult>
{
    /// <summary>
    /// The content type recorded alongside the captured bytes, for diagnostics and parity with the
    /// HTTP path. May be null when the encoding is implicit.
    /// </summary>
    string? ContentType { get; }

    /// <summary>Serialize a result into the bytes that will be stored.</summary>
    /// <param name="result">The result produced by the operation.</param>
    /// <returns>The encoded result.</returns>
    ReadOnlyMemory<byte> Serialize(TResult result);

    /// <summary>Reconstruct a result from previously stored bytes.</summary>
    /// <param name="payload">The bytes produced by an earlier <see cref="Serialize"/> call.</param>
    /// <returns>The decoded result.</returns>
    TResult Deserialize(ReadOnlySpan<byte> payload);
}

/// <summary>
/// An <see cref="IIdempotentResultCodec{TResult}"/> built from a serialize and a deserialize
/// delegate, so callers can supply a codec inline without declaring a type.
/// </summary>
/// <typeparam name="TResult">The operation result type.</typeparam>
public sealed class DelegateResultCodec<TResult> : IIdempotentResultCodec<TResult>
{
    private readonly Func<TResult, ReadOnlyMemory<byte>> serialize;
    private readonly SpanDeserializer deserialize;

    /// <summary>
    /// A deserialize function over a <see cref="ReadOnlySpan{T}"/>. A plain
    /// <see cref="Func{T, TResult}"/> cannot take a ref struct parameter, so this named delegate is
    /// used instead.
    /// </summary>
    /// <param name="payload">The stored bytes to decode.</param>
    /// <returns>The decoded result.</returns>
    public delegate TResult SpanDeserializer(ReadOnlySpan<byte> payload);

    /// <summary>Create a codec from a serialize and deserialize pair.</summary>
    /// <param name="serialize">Encodes a result into the bytes to store.</param>
    /// <param name="deserialize">Decodes stored bytes back into a result.</param>
    /// <param name="contentType">The content type to record; optional.</param>
    public DelegateResultCodec(
        Func<TResult, ReadOnlyMemory<byte>> serialize,
        SpanDeserializer deserialize,
        string? contentType = null)
    {
        ArgumentNullException.ThrowIfNull(serialize);
        ArgumentNullException.ThrowIfNull(deserialize);
        this.serialize = serialize;
        this.deserialize = deserialize;
        ContentType = contentType;
    }

    /// <inheritdoc />
    public string? ContentType { get; }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Serialize(TResult result) => serialize(result);

    /// <inheritdoc />
    public TResult Deserialize(ReadOnlySpan<byte> payload) => deserialize(payload);
}

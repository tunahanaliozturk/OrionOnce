namespace Moongazing.OrionOnce.Tests;

using System.Text;

using Moongazing.OrionOnce;

using Xunit;

public sealed class DelegateResultCodecTests
{
    [Fact]
    public void A_codec_round_trips_a_value_through_its_delegates()
    {
        var codec = new DelegateResultCodec<string>(
            serialize: s => Encoding.UTF8.GetBytes(s),
            deserialize: payload => Encoding.UTF8.GetString(payload),
            contentType: "text/plain");

        var encoded = codec.Serialize("orion");
        var decoded = codec.Deserialize(encoded.Span);

        Assert.Equal("orion", decoded);
        Assert.Equal("text/plain", codec.ContentType);
    }

    [Fact]
    public void The_content_type_defaults_to_null()
    {
        var codec = new DelegateResultCodec<int>(
            serialize: _ => ReadOnlyMemory<byte>.Empty,
            deserialize: _ => 0);

        Assert.Null(codec.ContentType);
    }

    [Fact]
    public void A_null_serialize_delegate_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DelegateResultCodec<int>(null!, _ => 0));
    }

    [Fact]
    public void A_null_deserialize_delegate_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DelegateResultCodec<int>(_ => ReadOnlyMemory<byte>.Empty, null!));
    }
}

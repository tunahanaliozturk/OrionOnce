namespace Moongazing.OrionOnce.Tests;

using System.Text;

using Moongazing.OrionOnce.Storage;

using Xunit;

public sealed class CachedResponseTests
{
    [Fact]
    public void A_cached_response_round_trips_its_fields()
    {
        var body = Encoding.UTF8.GetBytes("{\"ok\":true}");
        var response = new CachedResponse
        {
            StatusCode = 201,
            ContentType = "application/json",
            Body = body,
        };

        Assert.Equal(201, response.StatusCode);
        Assert.Equal("application/json", response.ContentType);
        Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(response.Body.Span));
    }

    [Fact]
    public void A_content_type_is_optional()
    {
        var response = new CachedResponse
        {
            StatusCode = 204,
            ContentType = null,
            Body = ReadOnlyMemory<byte>.Empty,
        };

        Assert.Null(response.ContentType);
        Assert.True(response.Body.IsEmpty);
    }

    [Fact]
    public void An_empty_body_is_supported()
    {
        var response = new CachedResponse
        {
            StatusCode = 200,
            Body = ReadOnlyMemory<byte>.Empty,
        };

        Assert.Equal(0, response.Body.Length);
    }
}

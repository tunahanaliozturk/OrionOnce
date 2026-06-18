namespace Moongazing.OrionOnce.Tests;

using Moongazing.OrionOnce;

using Xunit;

public sealed class IdempotencyOptionsTests
{
    [Fact]
    public void Defaults_are_the_documented_values()
    {
        var options = new IdempotencyOptions();

        Assert.Equal("Idempotency-Key", options.HeaderName);
        Assert.Equal(TimeSpan.FromHours(24), options.Retention);
        Assert.Equal(1024 * 1024, options.MaxBodyBytes);
        Assert.False(options.RequireKey);
    }

    [Fact]
    public void The_default_guarded_methods_are_the_mutating_verbs()
    {
        var options = new IdempotencyOptions();

        Assert.Contains("POST", options.Methods);
        Assert.Contains("PUT", options.Methods);
        Assert.Contains("PATCH", options.Methods);
        Assert.Contains("DELETE", options.Methods);
        Assert.Equal(4, options.Methods.Count);
    }

    [Fact]
    public void Idempotent_read_verbs_are_not_guarded_by_default()
    {
        var options = new IdempotencyOptions();

        Assert.DoesNotContain("GET", options.Methods);
        Assert.DoesNotContain("HEAD", options.Methods);
        Assert.DoesNotContain("OPTIONS", options.Methods);
    }

    [Fact]
    public void The_method_set_is_case_insensitive()
    {
        var options = new IdempotencyOptions();

        Assert.Contains("post", options.Methods);
        Assert.Contains("Post", options.Methods);
    }

    [Fact]
    public void The_method_set_can_be_customised()
    {
        var options = new IdempotencyOptions();
        options.Methods.Clear();
        options.Methods.Add("GET");

        Assert.Single(options.Methods);
        Assert.Contains("get", options.Methods);
    }

    [Fact]
    public void Validate_accepts_the_defaults()
    {
        var options = new IdempotencyOptions();

        var exception = Record.Exception(options.Validate);

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Validate_rejects_a_null_or_empty_header_name(string? header)
    {
        var options = new IdempotencyOptions { HeaderName = header! };

        Assert.ThrowsAny<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Validate_rejects_a_zero_retention()
    {
        var options = new IdempotencyOptions { Retention = TimeSpan.Zero };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void Validate_rejects_a_negative_retention()
    {
        var options = new IdempotencyOptions { Retention = TimeSpan.FromSeconds(-1) };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_rejects_a_non_positive_max_body(int maxBody)
    {
        var options = new IdempotencyOptions { MaxBodyBytes = maxBody };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void Validate_accepts_a_one_byte_max_body()
    {
        var options = new IdempotencyOptions { MaxBodyBytes = 1 };

        var exception = Record.Exception(options.Validate);

        Assert.Null(exception);
    }
}

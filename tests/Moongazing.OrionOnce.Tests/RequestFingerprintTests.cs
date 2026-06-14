namespace Moongazing.OrionOnce.Tests;

using System.Text;

using Moongazing.OrionOnce;

using Xunit;

public sealed class RequestFingerprintTests
{
    [Fact]
    public void Identical_requests_share_a_fingerprint()
    {
        var a = RequestFingerprint.Compute("POST", "/orders", Encoding.UTF8.GetBytes("body"));
        var b = RequestFingerprint.Compute("POST", "/orders", Encoding.UTF8.GetBytes("body"));

        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
    }

    [Fact]
    public void Method_is_case_insensitive()
    {
        var a = RequestFingerprint.Compute("post", "/orders", Encoding.UTF8.GetBytes("body"));
        var b = RequestFingerprint.Compute("POST", "/orders", Encoding.UTF8.GetBytes("body"));

        Assert.Equal(a, b);
    }

    [Fact]
    public void A_different_body_changes_the_fingerprint()
    {
        var a = RequestFingerprint.Compute("POST", "/orders", Encoding.UTF8.GetBytes("body-a"));
        var b = RequestFingerprint.Compute("POST", "/orders", Encoding.UTF8.GetBytes("body-b"));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void A_different_path_changes_the_fingerprint()
    {
        var a = RequestFingerprint.Compute("POST", "/orders", Encoding.UTF8.GetBytes("body"));
        var b = RequestFingerprint.Compute("POST", "/invoices", Encoding.UTF8.GetBytes("body"));

        Assert.NotEqual(a, b);
    }
}

namespace Moongazing.OrionOnce.Tests;

using System.Text;

using Moongazing.OrionOnce;

using Xunit;

public sealed class RequestFingerprintExtraTests
{
    [Fact]
    public void A_different_method_changes_the_fingerprint()
    {
        var post = RequestFingerprint.Compute("POST", "/orders", Encoding.UTF8.GetBytes("body"));
        var put = RequestFingerprint.Compute("PUT", "/orders", Encoding.UTF8.GetBytes("body"));

        Assert.NotEqual(post, put);
    }

    [Fact]
    public void An_empty_body_still_produces_a_full_digest()
    {
        var fingerprint = RequestFingerprint.Compute("POST", "/orders", ReadOnlySpan<byte>.Empty);

        Assert.Equal(64, fingerprint.Length);
    }

    [Fact]
    public void Two_empty_body_requests_share_a_fingerprint()
    {
        var a = RequestFingerprint.Compute("POST", "/orders", ReadOnlySpan<byte>.Empty);
        var b = RequestFingerprint.Compute("POST", "/orders", Array.Empty<byte>());

        Assert.Equal(a, b);
    }

    [Fact]
    public void The_digest_is_lowercase_hex()
    {
        var fingerprint = RequestFingerprint.Compute("POST", "/orders", Encoding.UTF8.GetBytes("body"));

        Assert.Matches("^[0-9a-f]{64}$", fingerprint);
    }

    [Fact]
    public void Path_is_case_sensitive()
    {
        // Only the method is upper-cased; the path is hashed verbatim, so casing matters.
        var lower = RequestFingerprint.Compute("POST", "/orders", Encoding.UTF8.GetBytes("body"));
        var upper = RequestFingerprint.Compute("POST", "/Orders", Encoding.UTF8.GetBytes("body"));

        Assert.NotEqual(lower, upper);
    }

    [Fact]
    public void The_query_string_is_part_of_the_path_component()
    {
        // The middleware passes path + query as the path argument; different queries must differ.
        var a = RequestFingerprint.Compute("GET", "/orders?page=1", Encoding.UTF8.GetBytes("body"));
        var b = RequestFingerprint.Compute("GET", "/orders?page=2", Encoding.UTF8.GetBytes("body"));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void The_method_path_separator_prevents_field_run_together_collisions()
    {
        // "POST" + "/a" must not collide with "POS" + "T/a"; the newline framing keeps them apart.
        var a = RequestFingerprint.Compute("POST", "/a", Encoding.UTF8.GetBytes("body"));
        var b = RequestFingerprint.Compute("POST", "\n/a", Encoding.UTF8.GetBytes("body"));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void A_null_method_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            RequestFingerprint.Compute(null!, "/orders", Encoding.UTF8.GetBytes("body")));
    }

    [Fact]
    public void A_null_path_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            RequestFingerprint.Compute("POST", null!, Encoding.UTF8.GetBytes("body")));
    }

    [Fact]
    public void Mixed_case_methods_normalise_to_the_same_digest()
    {
        var a = RequestFingerprint.Compute("PoSt", "/orders", Encoding.UTF8.GetBytes("body"));
        var b = RequestFingerprint.Compute("POST", "/orders", Encoding.UTF8.GetBytes("body"));

        Assert.Equal(a, b);
    }

    [Fact]
    public void A_known_input_produces_a_stable_digest()
    {
        // Locks the wire format: SHA-256 over "POST\n/orders\n" + body. A change here is a
        // breaking change for any persisted store, so pin the exact value.
        var expected = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes("POST\n/orders\nbody"));
        var expectedHex = Convert.ToHexString(expected).ToLowerInvariant();

        var actual = RequestFingerprint.Compute("POST", "/orders", Encoding.UTF8.GetBytes("body"));

        Assert.Equal(expectedHex, actual);
    }

    [Fact]
    public void A_large_body_produces_a_stable_digest()
    {
        // A body well over the 256-byte stack threshold forces assembly through the pooled
        // (rented) buffer, which is cleared on return. The digest must remain byte-identical
        // to an independently computed SHA-256 over "POST\n/orders\n" + body.
        var body = Encoding.UTF8.GetBytes(new string('x', 4096));

        var expected = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes("POST\n/orders\n").Concat(body).ToArray());
        var expectedHex = Convert.ToHexString(expected).ToLowerInvariant();

        var actual = RequestFingerprint.Compute("POST", "/orders", body);

        Assert.Equal(expectedHex, actual);
    }
}

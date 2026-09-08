using System.Security.Cryptography;
using System.Text;
using AgentStudio.Infrastructure;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>Exercises the key-parameterized Protect/UnprotectWithKey methods, not the static
/// Configure/Protect/Unprotect wrappers — those share process-global state that
/// AddAgentStudioInfrastructure (via ApiEndpointTests' WebApplicationFactory) also mutates, and
/// xUnit runs different test classes in parallel by default, so touching that shared static
/// here would be a real race, not a hypothetical one.</summary>
public class SecretProtectorTests
{
    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Round_trips_plaintext_through_encryption()
    {
        var key = NewKey();
        var ciphertext = SecretProtector.ProtectWithKey("sk-super-secret-value", key);

        Assert.NotEqual("sk-super-secret-value", ciphertext);
        Assert.DoesNotContain("sk-super-secret-value", ciphertext);
        Assert.Equal("sk-super-secret-value", SecretProtector.UnprotectWithKey(ciphertext, key));
    }

    [Fact]
    public void Same_plaintext_encrypts_differently_each_time()
    {
        var key = NewKey();
        var a = SecretProtector.ProtectWithKey("same-value", key);
        var b = SecretProtector.ProtectWithKey("same-value", key);

        Assert.NotEqual(a, b); // random nonce per call — not a deterministic cipher
        Assert.Equal("same-value", SecretProtector.UnprotectWithKey(a, key));
        Assert.Equal("same-value", SecretProtector.UnprotectWithKey(b, key));
    }

    [Fact]
    public void Null_key_leaves_value_untouched()
    {
        Assert.Equal("plain", SecretProtector.ProtectWithKey("plain", null));
        Assert.Equal("plain", SecretProtector.UnprotectWithKey("plain", null));
    }

    [Fact]
    public void Null_plaintext_stays_null()
    {
        var key = NewKey();
        Assert.Null(SecretProtector.ProtectWithKey(null, key));
    }

    [Fact]
    public void Unprotect_treats_pre_encryption_plaintext_row_as_already_plaintext()
    {
        // A row written before encryption was enabled — with a key now configured, decrypting
        // it must not crash: this is the exact FormFieldsJson-legacy-value bug pattern applied
        // proactively here instead of being found the hard way in production.
        var key = NewKey();
        Assert.Equal("sk-legacy-plaintext-key", SecretProtector.UnprotectWithKey("sk-legacy-plaintext-key", key));
    }

    [Fact]
    public void Unprotect_treats_arbitrary_base64_that_isnt_our_format_as_plaintext()
    {
        var key = NewKey();
        var arbitraryBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("just happens to be base64"));

        Assert.Equal(arbitraryBase64, SecretProtector.UnprotectWithKey(arbitraryBase64, key));
    }

    [Fact]
    public void Tampered_ciphertext_throws_instead_of_returning_garbage()
    {
        var key = NewKey();
        var ciphertext = SecretProtector.ProtectWithKey("sk-secret", key)!;
        var bytes = Convert.FromBase64String(ciphertext);
        bytes[^1] ^= 0xFF; // flip a bit in the ciphertext — must fail GCM auth, not decode to junk
        var tampered = Convert.ToBase64String(bytes);

        Assert.ThrowsAny<CryptographicException>(() => SecretProtector.UnprotectWithKey(tampered, key));
    }

    [Fact]
    public void Wrong_key_fails_to_decrypt()
    {
        var ciphertext = SecretProtector.ProtectWithKey("sk-secret", NewKey())!;

        Assert.ThrowsAny<CryptographicException>(() => SecretProtector.UnprotectWithKey(ciphertext, NewKey()));
    }
}

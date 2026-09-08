using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Infrastructure;

/// <summary>AES-256-GCM encryption for secrets stored at rest (today: only
/// ModelProviderConfig.ApiKey). Key comes from config/env ("Secrets:EncryptionKey", base64,
/// 32 bytes) — never hardcoded, never in source.
///
/// Configure/Protect/Unprotect use a static holder, not a DI-injected scoped service: EF Core
/// builds and caches its model (including every ValueConverter) once per process, keyed by
/// DbContextOptions — a converter that captured a per-request DI-resolved service would
/// silently keep using whichever scope happened to build the model first. The encryption key
/// is genuinely process-global config, not request state, so a static holder is the correct
/// fit for the converter's use, not a workaround. The actual crypto logic is factored into
/// key-parameterized methods below so it can be unit-tested without touching that shared state.
/// </summary>
public static class SecretProtector
{
    private const byte FormatMarker = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static byte[]? _key;

    /// <summary>Call once at startup with Secrets:EncryptionKey. Null/empty leaves encryption
    /// off — Protect/Unprotect become no-ops (dev/CI convenience, not a silent security
    /// downgrade in production since DEPLOYMENT.md requires setting the key there).</summary>
    public static void Configure(string? base64Key) => _key = ParseKey(base64Key);

    public static bool IsConfigured => _key is not null;

    public static string? Protect(string? plaintext) => ProtectWithKey(plaintext, _key);
    public static string? Unprotect(string? stored) => UnprotectWithKey(stored, _key);

    public static byte[]? ParseKey(string? base64Key) =>
        string.IsNullOrWhiteSpace(base64Key) ? null : Convert.FromBase64String(base64Key);

    public static string? ProtectWithKey(string? plaintext, byte[]? key)
    {
        if (plaintext is null || key is null) return plaintext;

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var combined = new byte[1 + NonceSize + TagSize + cipherBytes.Length];
        combined[0] = FormatMarker;
        Buffer.BlockCopy(nonce, 0, combined, 1, NonceSize);
        Buffer.BlockCopy(tag, 0, combined, 1 + NonceSize, TagSize);
        Buffer.BlockCopy(cipherBytes, 0, combined, 1 + NonceSize + TagSize, cipherBytes.Length);
        return Convert.ToBase64String(combined);
    }

    /// <summary>Tolerant of anything that isn't our own encrypted format — a row written before
    /// encryption was enabled (or written while no key was configured) is still plain text in
    /// the column, and must come back as-is instead of crashing every page that reads it (same
    /// lesson as the FormFieldsJson legacy-value bug: the getter can't assume the stored shape
    /// matches what the current code always writes).</summary>
    public static string? UnprotectWithKey(string? stored, byte[]? key)
    {
        if (stored is null || key is null) return stored;

        byte[] combined;
        try
        {
            combined = Convert.FromBase64String(stored);
        }
        catch (FormatException)
        {
            return stored;
        }

        if (combined.Length < 1 + NonceSize + TagSize || combined[0] != FormatMarker)
            return stored;

        var nonce = combined.AsSpan(1, NonceSize);
        var tag = combined.AsSpan(1 + NonceSize, TagSize);
        var cipherBytes = combined.AsSpan(1 + NonceSize + TagSize);
        var plainBytes = new byte[cipherBytes.Length];

        using (var aes = new AesGcm(key, TagSize))
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes); // throws CryptographicException if tampered — never returns garbage

        return Encoding.UTF8.GetString(plainBytes);
    }
}

using System.Security.Cryptography;
using System.Text;

namespace ApiScheduler.Core.Security;

/// <summary>
/// Protects secrets (API keys, SQL passwords) with Windows DPAPI, machine scope.
///
/// Why not a hash: the service must send the real key in the HTTP header, and a hash is one-way.
/// So the secret is ENCRYPTED (only decryptable on this server) and we additionally keep a SHA-256
/// HASH (fingerprint) that is safe to display so admins can verify which key is stored.
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "dpapi:v1:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ApiScheduler::secret::v1::7f3c1e9a");

    public static string Protect(string plainText)
    {
        ArgumentException.ThrowIfNullOrEmpty(plainText);
        var data = Encoding.UTF8.GetBytes(plainText);
        try
        {
            var cipher = ProtectedData.Protect(data, Entropy, DataProtectionScope.LocalMachine);
            return Prefix + Convert.ToBase64String(cipher);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    public static string Unprotect(string protectedText)
    {
        if (string.IsNullOrEmpty(protectedText) || !protectedText.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("Secret is not in the expected protected format.");

        var cipher = Convert.FromBase64String(protectedText[Prefix.Length..]);
        var data = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.LocalMachine);
        try
        {
            return Encoding.UTF8.GetString(data);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    /// <summary>Non-reversible fingerprint for display, e.g. "SHA256:3F9A…C21B".</summary>
    public static string Fingerprint(string plainText)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(plainText));
        var hex = Convert.ToHexString(hash);
        return $"SHA256:{hex[..8]}…{hex[^8..]}";
    }
}

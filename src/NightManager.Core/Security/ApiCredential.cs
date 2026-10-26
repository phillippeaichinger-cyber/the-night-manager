namespace NightManager.Core.Security;

/// <summary>
/// Observatory credential issued by the backend at pairing: <c>nm2_&lt;keyId&gt;_&lt;secret&gt;</c>.
/// The key id identifies the credential and is sent with each request; the secret only
/// ever leaves this machine as an HMAC signature.
/// </summary>
public sealed class ApiCredential {
    private const string Prefix = "nm2_";

    private ApiCredential(string keyId, string secret) {
        KeyId = keyId;
        Secret = secret;
    }

    public string KeyId { get; }

    public string Secret { get; }

    public string Value => $"{Prefix}{KeyId}_{Secret}";

    /// <summary>Display-safe form, e.g. "nm2_a1b2c3d4…".</summary>
    public string Redacted => $"{Prefix}{KeyId}…";

    public static bool TryParse(string? value, out ApiCredential? credential) {
        credential = null;

        if (string.IsNullOrWhiteSpace(value)) {
            return false;
        }

        var trimmed = value.Trim();

        if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal)) {
            return false;
        }

        var rest = trimmed[Prefix.Length..];
        var separator = rest.IndexOf('_');

        if (separator <= 0 || separator == rest.Length - 1) {
            return false;
        }

        var keyId = rest[..separator];
        var secret = rest[(separator + 1)..];

        if (!IsToken(keyId) || !IsToken(secret) || secret.Length < 32) {
            return false;
        }

        credential = new ApiCredential(keyId, secret);
        return true;
    }

    private static bool IsToken(string value) => value.All(c => char.IsAsciiLetterOrDigit(c));
}

using System.Security.Cryptography;
using System.Text;
using NightManager.Core.Json;

namespace NightManager.Core.Security;

public sealed record SignedHeaders(string KeyId, string Timestamp, string Signature);

/// <summary>
/// Signs requests as HMAC-SHA256(secret, "NM2\n{METHOD}\n{path}\n{timestamp}\n{hex(sha256(body))}").
/// Binding method and path prevents replaying a captured body against another endpoint;
/// the backend rejects timestamps outside ±5 minutes and de-duplicates batch ids.
/// </summary>
public static class RequestSigner {
    public const string KeyHeader = "x-nm-key";
    public const string TimestampHeader = "x-nm-timestamp";
    public const string SignatureHeader = "x-nm-signature";

    /// <summary>
    /// The signed path is the request path from "/v2/" onwards, so a signature does not depend on
    /// how the API is reached (custom domain "/v2/ingest", function URL "/api/v2/ingest", emulator
    /// "/project/region/api/v2/ingest").
    /// </summary>
    public static string CanonicalPath(string absolutePath) {
        var index = absolutePath.IndexOf("/v2/", StringComparison.Ordinal);
        return index >= 0 ? absolutePath[index..] : absolutePath;
    }

    public static SignedHeaders Sign(ApiCredential credential, string method, string path, ReadOnlySpan<byte> body, DateTimeOffset now) {
        var timestamp = Timestamps.Format(now);
        var signature = ComputeSignature(credential.Secret, method, CanonicalPath(path), timestamp, body);

        return new SignedHeaders(credential.KeyId, timestamp, signature);
    }

    public static string ComputeSignature(string secret, string method, string path, string timestamp, ReadOnlySpan<byte> body) {
        var bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var canonical = $"NM2\n{method.ToUpperInvariant()}\n{path}\n{timestamp}\n{bodyHash}";
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical));

        return Convert.ToHexString(mac).ToLowerInvariant();
    }
}

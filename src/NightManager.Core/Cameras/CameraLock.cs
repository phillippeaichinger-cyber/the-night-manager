using System.Security.Cryptography;
using System.Text;

namespace NightManager.Core.Cameras;

/// <summary>
/// Locks a camera's settings to whoever holds its credentials. Locking stores a salted PBKDF2 hash of the
/// camera's username and password (or a folder's passcode), never the secret itself. Unlocking needs the same
/// secret again; for network cameras the host may also accept credentials the camera itself accepts (so an
/// observatory that changes the camera's password is never locked out).
/// </summary>
public static class CameraLock {
    private const int Iterations = 120_000;

    /// <summary>The secret a camera is locked with: "username\npassword" for network cameras, the passcode for a folder.</summary>
    public static string Secret(string userName, string password) => $"{userName?.Trim()}\n{password}";

    public static CameraSettings Lock(CameraSettings camera, string secret) {
        if (string.IsNullOrEmpty(secret) || secret == "\n") {
            throw new ArgumentException("A password is needed to lock the camera settings.", nameof(secret));
        }
        var salt = RandomNumberGenerator.GetBytes(16);
        return camera with { Locked = true, LockSalt = Convert.ToBase64String(salt), LockHash = Convert.ToBase64String(Hash(secret, salt)) };
    }

    public static bool Matches(CameraSettings camera, string secret) {
        if (!camera.Locked || camera.LockSalt is null || camera.LockHash is null || string.IsNullOrEmpty(secret)) {
            return false;
        }
        try {
            var expected = Convert.FromBase64String(camera.LockHash);
            return CryptographicOperations.FixedTimeEquals(expected, Hash(secret, Convert.FromBase64String(camera.LockSalt)));
        } catch (FormatException) {
            return false;
        }
    }

    public static CameraSettings Unlock(CameraSettings camera) => camera with { Locked = false, LockSalt = null, LockHash = null };

    /// <summary>
    /// Whether an edit touches anything a locked camera protects. Only the display name is free; on/off, source,
    /// credentials, mode, interval, size and width need an unlock.
    /// </summary>
    public static bool ChangesProtectedFields(CameraSettings before, CameraSettings after) =>
        before.Enabled != after.Enabled || before.Kind != after.Kind || before.Address != after.Address || before.UserName != after.UserName ||
        before.Mode != after.Mode || before.IntervalMinutes != after.IntervalMinutes ||
        before.MaxKilobytes != after.MaxKilobytes || before.Width != after.Width;

    private static byte[] Hash(string secret, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, Iterations, HashAlgorithmName.SHA256, 32);
}

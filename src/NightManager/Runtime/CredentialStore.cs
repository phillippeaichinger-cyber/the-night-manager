using NightManager.Core.Security;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace NightManager.Plugin.Runtime {
    /// <summary>
    /// Stores the observatory credential encrypted with Windows DPAPI (current user scope),
    /// so it is unreadable to other Windows accounts and useless if the file is copied elsewhere.
    /// Uses crypt32 directly to avoid shipping an extra assembly.
    /// </summary>
    internal sealed class CredentialStore {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NightManager.Credential.v2");
        private readonly string path;

        public CredentialStore(string path) {
            this.path = path;
        }

        public DateTime LastWriteTimeUtc => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

        public ApiCredential Load() {
            try {
                if (!File.Exists(path)) {
                    return null;
                }

                var plain = Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(path)));
                return ApiCredential.TryParse(plain, out var credential) ? credential : null;
            } catch (Exception ex) {
                NinaLog.Instance.Warning($"Stored credential could not be read and will be ignored: {ex.Message}");
                return null;
            }
        }

        public void Save(ApiCredential credential) {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, Protect(Encoding.UTF8.GetBytes(credential.Value)));
            File.Move(temporary, path, overwrite: true);
        }

        public void Delete() {
            if (File.Exists(path)) {
                File.Delete(path);
            }
        }

        private static byte[] Protect(byte[] data) => Transform(data, protect: true, Entropy);

        private static byte[] Unprotect(byte[] data) => Transform(data, protect: false, Entropy);

        /// <summary>DPAPI (current user) for other small secrets, e.g. camera passwords, each with its own entropy.</summary>
        internal static byte[] ProtectBytes(byte[] data, byte[] entropy) => Transform(data, protect: true, entropy);

        internal static byte[] UnprotectBytes(byte[] data, byte[] entropy) => Transform(data, protect: false, entropy);

        private static byte[] Transform(byte[] data, bool protect, byte[] entropyBytes) {
            var input = ToBlob(data);
            var entropy = ToBlob(entropyBytes);
            var output = default(DataBlob);

            try {
                var ok = protect
                    ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);

                if (!ok) {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            } finally {
                Marshal.FreeHGlobal(input.Data);
                Marshal.FreeHGlobal(entropy.Data);

                if (output.Data != IntPtr.Zero) {
                    LocalFree(output.Data);
                }
            }
        }

        private static DataBlob ToBlob(byte[] data) {
            var blob = new DataBlob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
            Marshal.Copy(data, 0, blob.Data, data.Length);
            return blob;
        }

        private const int UiForbidden = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob dataIn, string description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}

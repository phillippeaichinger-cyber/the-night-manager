using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using NightManager.Core.Contracts;
using NightManager.Core.Logging;
using NightManager.Core.Security;
using NightManager.Core.Settings;
using NightManager.Core.Startup;
using NightManager.Core.Sync;
using NightManager.Core.Syncthing;

namespace NightManager.Launcher;

/// <summary>
/// Runs once at Windows logon (registered by the plugin when "Start NINA when this PC starts" is on): reports "PC up",
/// fetches the startup plan saved in the app, and starts NINA's profiles in order. Exits when done.
/// </summary>
internal static class Program {
    private static int Main(string[] args) {
        // One Launcher at a time (a second logon, or started by hand while one runs).
        using var single = new Mutex(initiallyOwned: true, @"Local\NightManager.Launcher", out var first);
        if (!first) return 0;

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "NightManager");
        var log = new FileLog(Path.Combine(root, "launcher", "launcher.log"));
        try {
            var installation = InstallationSettings.Load(Path.Combine(root, "settings.json"));
            // Syncthing first: it starts at logon too, and would upload freely until NINA opens and the plugin takes
            // over. Outside the upload window it's paused (or limited) now, exactly as the plugin would.
            var syncthing = Task.Run(() => SyncthingAtBoot.Apply(installation, root, log));
            var settings = installation.Launcher;
            // --now: run even if switched off on this PC (the plugin's "Test now" button).
            if (!settings.Enabled && !args.Contains("--now")) {
                log.Info("Starting NINA at logon is off on this PC.");
                syncthing.Wait(TimeSpan.FromMinutes(4));
                return 0;
            }
            var inventory = StartupInventoryBuilder.Build(settings with { Enabled = true }, root,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "0";
            using var transport = new HttpApiTransport(new Uri(installation.ServiceUrl), $"NightManager-Launcher/{version}");
            var credentials = Path.Combine(root, "credential.bin");
            var runner = new LauncherRunner(new WindowsHost(log), transport, () => Credential.Load(credentials, log), Path.Combine(root, "launcher", "plan-cache.json"), log, version);

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var result = runner.RunAsync(new LauncherRunner.Inputs {
                Settings = settings with { Enabled = true },
                Inventory = inventory,
                OpenSessions = StartupInventoryBuilder.OpenSessions(root, inventory.Profiles),
            }, timeout.Token).GetAwaiter().GetResult();
            log.Info($"Done: {result.Status.Phase}{(result.Status.Message is null ? "" : " (" + result.Status.Message + ")")}" + (result.UsedCachedPlan ? ", using the saved plan" : ""));
            syncthing.Wait(TimeSpan.FromMinutes(4));
            return result.Status.Phase == "failed" ? 1 : 0;
        } catch (Exception ex) {
            log.Error("Launcher failed", ex);
            return 2;
        }
    }
}

/// <summary>NINA's processes (their command lines say which profile) and starting one, on the logged-on desktop.</summary>
internal sealed class WindowsHost(INmLog log) : ILauncherHost {
    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public Task Delay(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);

    public IReadOnlySet<string> RunningProfiles() {
        var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try {
            using var search = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE Name = 'NINA.exe'");
            foreach (var process in search.Get()) {
                var line = process["CommandLine"] as string ?? string.Empty;
                // A NINA started by hand without --profileid opens its last profile, which can't be told from here.
                profiles.Add(ProfileOf(line) ?? LaunchPlanner.UnknownProfile);
            }
        } catch (Exception ex) {
            log.Warning($"Couldn't list NINA's processes: {ex.Message}");
        }
        return profiles;
    }

    internal static string? ProfileOf(string commandLine) {
        var parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++) {
            if (parts[i] is "--profileid" or "-p" && i + 1 < parts.Length) return parts[i + 1].Trim('"');
            if (parts[i].StartsWith("--profileid=", StringComparison.Ordinal)) return parts[i]["--profileid=".Length..].Trim('"');
        }
        return null;
    }

    public bool Start(string ninaPath, IReadOnlyList<string> arguments) {
        try {
            var start = new ProcessStartInfo(ninaPath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(ninaPath) ?? string.Empty };
            foreach (var arg in arguments) start.ArgumentList.Add(arg);
            return Process.Start(start) is not null;
        } catch (Exception ex) {
            log.Error($"Couldn't start {ninaPath}", ex);
            return false;
        }
    }
}

/// <summary>The PC's credential, stored by the plugin with Windows' per-user protection (DPAPI).</summary>
internal static class Credential {
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NightManager.Credential.v2");

    public static ApiCredential? Load(string path, INmLog log) {
        try {
            if (!File.Exists(path)) return null;
            var plain = Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(path)));
            return ApiCredential.TryParse(plain, out var credential) ? credential : null;
        } catch (Exception ex) {
            log.Warning($"The stored credential couldn't be read: {ex.Message}");
            return null;
        }
    }

    private static byte[] Unprotect(byte[] data) {
        var input = Blob(data);
        var entropy = Blob(Entropy);
        var output = default(DataBlob);
        try {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 0x1, ref output)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        } finally {
            Marshal.FreeHGlobal(input.Data);
            Marshal.FreeHGlobal(entropy.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    private static DataBlob Blob(byte[] data) {
        var blob = new DataBlob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, blob.Data, data.Length);
        return blob;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DataBlob {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}

/// <summary>The Launcher's own log next to the plan cache (kept to the last ~200 KB).</summary>
internal sealed class FileLog : INmLog {
    private readonly string path;

    public FileLog(string path) {
        this.path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { if (File.Exists(path) && new FileInfo(path).Length > 200_000) File.Move(path, path + ".old", overwrite: true); } catch (IOException) { }
    }

    private void Write(string level, string message) {
        try { File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}"); } catch (IOException) { }
    }

    public void Debug(string message) => Write("DEBUG", message);
    public void Info(string message) => Write("INFO", message);
    public void Warning(string message) => Write("WARN", message);
    public void Error(string message, Exception? exception = null) => Write("ERROR", exception is null ? message : $"{message}: {exception}");
}

/// <summary>
/// The Syncthing upload window at logon (the plugin's own rules and state, so NINA's plugin carries on from here):
/// outside the window, Syncthing is paused or limited before it can start uploading. Syncthing starts at logon too,
/// so it's given a few minutes to answer. Skipped when a NINA already holds the Syncthing lock (it's in charge).
/// </summary>
internal static class SyncthingAtBoot {
    public static void Apply(InstallationSettings installation, string root, INmLog log) {
        var options = installation.Syncthing;
        if (!options.Enabled) {
            log.Info("Syncthing: not watched by Night Manager on this PC.");
            return;
        }
        if (!options.Window.Enabled) {
            log.Info("Syncthing: no upload window set; left as it is.");
            return;
        }
        FileStream? held = null;
        try {
            try {
                held = new FileStream(Path.Combine(root, "syncthing.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            } catch (IOException) {
                log.Info("Syncthing: a NINA on this PC is already looking after it.");
                return;
            }
            var key = string.IsNullOrWhiteSpace(options.ApiKey) ? SyncthingClient.ReadLocalApiKey() : options.ApiKey;
            if (string.IsNullOrWhiteSpace(key)) {
                log.Info("Syncthing: no API key found; can't check the upload window.");
                return;
            }
            using var client = new SyncthingClient(options.Url, key);
            var settingsPath = Path.Combine(root, "settings.json");
            var monitor = new SyncthingMonitor(() => client, () => InstallationSettings.Load(settingsPath).Syncthing,
                updated => (InstallationSettings.Load(settingsPath) with { Syncthing = updated }).Normalized().Save(settingsPath),
                Path.Combine(root, "syncthing-changes.json"), log: log);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            while (!timeout.IsCancellationRequested) {
                monitor.PollAsync(timeout.Token).GetAwaiter().GetResult();
                if (monitor.State is { } state && state.Status != SyncthingStatus.Unreachable) {
                    var inside = SyncSchedule.InWindow(options.Window, DateTime.Now);
                    log.Info($"Syncthing: {(inside ? "inside" : "outside")} the upload window ({options.Window.Start}–{options.Window.End}): {state.Status}.");
                    return;
                }
                Thread.Sleep(TimeSpan.FromSeconds(10));
            }
            log.Info("Syncthing: didn't answer within 3 minutes; NINA's plugin will apply the window once it's running.");
        } catch (Exception ex) {
            log.Error("Syncthing check at logon failed", ex);
        } finally {
            held?.Dispose();
        }
    }
}

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightManager.Core.Syncthing;

/// <summary>The few Syncthing REST calls Night Manager uses (https://docs.syncthing.net/dev/rest.html).</summary>
public interface ISyncthingApi {
    Task<string?> VersionAsync(CancellationToken token);
    Task<string> MyIdAsync(CancellationToken token);
    Task<IReadOnlyList<StDevice>> DevicesAsync(CancellationToken token);
    Task<IReadOnlyList<StFolder>> FoldersAsync(CancellationToken token);
    Task<StConnections> ConnectionsAsync(CancellationToken token);
    Task<StCompletion> CompletionAsync(string deviceId, CancellationToken token);
    Task<StFolderStatus> FolderStatusAsync(string folderId, CancellationToken token);
    Task<int> MaxSendKbpsAsync(CancellationToken token);
    Task SetDevicePausedAsync(string deviceId, bool paused, CancellationToken token);
    Task SetMaxSendKbpsAsync(int kbps, CancellationToken token);
}

public sealed record StDevice(string DeviceId, string Name, bool Paused);
public sealed record StFolder(string Id, string Label, bool Paused, IReadOnlyList<string> DeviceIds);
public sealed record StConnection(bool Connected, bool Paused, long InBytesTotal, long OutBytesTotal);
public sealed record StConnections(IReadOnlyDictionary<string, StConnection> Devices, long InBytesTotal, long OutBytesTotal);
public sealed record StCompletion(double Completion, long NeedBytes, long NeedItems);
public sealed record StFolderStatus(string? State, long GlobalBytes, long NeedBytes, int PullErrors);

public sealed class SyncthingClient : ISyncthingApi, IDisposable {
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient http;

    public SyncthingClient(string baseUrl, string apiKey, HttpMessageHandler? handler = null) {
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestHeaders.Add("X-API-Key", apiKey);
    }

    public async Task<string?> VersionAsync(CancellationToken token) =>
        (await Get("rest/system/version", token)).TryGetProperty("version", out var v) ? v.GetString() : null;

    public async Task<string> MyIdAsync(CancellationToken token) =>
        (await Get("rest/system/status", token)).GetProperty("myID").GetString() ?? string.Empty;

    public async Task<IReadOnlyList<StDevice>> DevicesAsync(CancellationToken token) =>
        (await Get("rest/config/devices", token)).EnumerateArray()
            .Select(d => new StDevice(Str(d, "deviceID"), Str(d, "name"), Bool(d, "paused"))).ToList();

    public async Task<IReadOnlyList<StFolder>> FoldersAsync(CancellationToken token) =>
        (await Get("rest/config/folders", token)).EnumerateArray()
            .Select(f => new StFolder(Str(f, "id"), Str(f, "label") is { Length: > 0 } l ? l : Str(f, "id"), Bool(f, "paused"),
                f.TryGetProperty("devices", out var ds) ? ds.EnumerateArray().Select(d => Str(d, "deviceID")).ToList() : [])).ToList();

    public async Task<StConnections> ConnectionsAsync(CancellationToken token) {
        var root = await Get("rest/system/connections", token);
        var devices = new Dictionary<string, StConnection>();
        if (root.TryGetProperty("connections", out var conns)) {
            foreach (var c in conns.EnumerateObject()) {
                devices[c.Name] = new StConnection(Bool(c.Value, "connected"), Bool(c.Value, "paused"), Long(c.Value, "inBytesTotal"), Long(c.Value, "outBytesTotal"));
            }
        }
        var total = root.TryGetProperty("total", out var t) ? t : default;
        return new StConnections(devices, Long(total, "inBytesTotal"), Long(total, "outBytesTotal"));
    }

    public async Task<StCompletion> CompletionAsync(string deviceId, CancellationToken token) {
        var c = await Get($"rest/db/completion?device={Uri.EscapeDataString(deviceId)}", token);
        return new StCompletion(c.TryGetProperty("completion", out var p) ? p.GetDouble() : 0, Long(c, "needBytes"), Long(c, "needItems"));
    }

    public async Task<StFolderStatus> FolderStatusAsync(string folderId, CancellationToken token) {
        var s = await Get($"rest/db/status?folder={Uri.EscapeDataString(folderId)}", token);
        return new StFolderStatus(Str(s, "state"), Long(s, "globalBytes"), Long(s, "needBytes"), (int)Long(s, "pullErrors"));
    }

    public async Task<int> MaxSendKbpsAsync(CancellationToken token) =>
        (int)Long(await Get("rest/config/options", token), "maxSendKbps");

    public Task SetDevicePausedAsync(string deviceId, bool paused, CancellationToken token) =>
        Patch($"rest/config/devices/{Uri.EscapeDataString(deviceId)}", new { paused }, token);

    public Task SetMaxSendKbpsAsync(int kbps, CancellationToken token) =>
        Patch("rest/config/options", new { maxSendKbps = kbps }, token);

    private async Task<JsonElement> Get(string path, CancellationToken token) {
        using var response = await http.GetAsync(path, token).ConfigureAwait(false);
        await Ensure(response, token).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Json, token).ConfigureAwait(false)).Clone();
    }

    private async Task Patch(string path, object body, CancellationToken token) {
        using var request = new HttpRequestMessage(HttpMethod.Patch, path) {
            Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
        };
        using var response = await http.SendAsync(request, token).ConfigureAwait(false);
        await Ensure(response, token).ConfigureAwait(false);
    }

    private static async Task Ensure(HttpResponseMessage response, CancellationToken token) {
        if (response.IsSuccessStatusCode) {
            return;
        }
        var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        throw new HttpRequestException(response.StatusCode switch {
            System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized => "Syncthing refused the API key.",
            _ => $"Syncthing answered {(int)response.StatusCode}: {text.Trim()}",
        });
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    /// <summary>The API key from Syncthing's own config on this PC (%LOCALAPPDATA%\Syncthing\config.xml).</summary>
    public static string? ReadLocalApiKey() {
        foreach (var dir in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData }) {
            var path = Path.Combine(Environment.GetFolderPath(dir), "Syncthing", "config.xml");
            if (!File.Exists(path)) {
                continue;
            }
            try {
                var key = System.Xml.Linq.XDocument.Load(path).Root?.Element("gui")?.Element("apikey")?.Value;
                if (!string.IsNullOrWhiteSpace(key)) {
                    return key.Trim();
                }
            } catch (Exception) {
                // Unreadable config: the user can paste the key in the options instead.
            }
        }
        return null;
    }

    public void Dispose() => http.Dispose();
}

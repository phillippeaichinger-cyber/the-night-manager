using System.Net;

namespace NightManager.Core.Cameras;

/// <summary>Takes one frame from an RTSP stream and returns it as an encoded picture (the host decodes it in memory).</summary>
public delegate Task<GrabResult> RtspFrameSource(string address, string userName, string? password, int width, CancellationToken token);

/// <summary>One exposure from a camera plugged into the PC (the host owns the driver), returned as an encoded picture.</summary>
public delegate Task<GrabResult> LocalCameraSource(CameraSettings camera, CancellationToken token);

public sealed record GrabResult(byte[]? Image, string? Error) {
    public static GrabResult Ok(byte[] image) => new(image, null);
    public static GrabResult Fail(string error) => new(null, error);
    public bool IsSuccess => Image is { Length: > 0 };
}

/// <summary>
/// Gets one picture from a camera, on the observatory's own network. Nothing streams: a snapshot address returns
/// a single JPEG, a folder gives its newest file, and RTSP connects, decodes one frame in memory and disconnects.
/// </summary>
public sealed class CameraGrabber {
    private const int MaxDownloadBytes = 16 * 1024 * 1024;
    private static readonly string[] FolderPatterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp"];
    private readonly Func<HttpMessageHandler>? handlerFactory;
    private readonly RtspFrameSource? rtsp;
    private readonly LocalCameraSource? zwo;

    /// <param name="handlerFactory">Tests pass a fake handler; null uses a real one with the camera's credentials.</param>
    /// <param name="rtsp">The host's RTSP decoder (the plugin bundles one); null: RTSP cameras aren't available.</param>
    /// <param name="zwo">The host's ZWO capture; null: ZWO cameras aren't available.</param>
    public CameraGrabber(Func<HttpMessageHandler>? handlerFactory = null, RtspFrameSource? rtsp = null, LocalCameraSource? zwo = null) {
        this.handlerFactory = handlerFactory;
        this.rtsp = rtsp;
        this.zwo = zwo;
    }

    public Task<GrabResult> GrabAsync(CameraSettings camera, string? password, DateTimeOffset now, CancellationToken token) =>
        camera.Kind switch {
            CameraSourceKind.Snapshot => SnapshotAsync(camera.Address, camera.UserName, password, token),
            CameraSourceKind.Folder => Task.FromResult(NewestInFolder(camera.Address, now)),
            CameraSourceKind.Rtsp => RtspFrameAsync(camera.Address, camera.UserName, password, camera.Width, token),
            CameraSourceKind.Zwo => zwo?.Invoke(camera, token) ?? Task.FromResult(GrabResult.Fail("ZWO cameras aren't available here")),
            _ => Task.FromResult(GrabResult.Fail("Unknown camera type")),
        };

    /// <summary>Do these credentials work on the camera? Used to unlock a locked camera with the camera's own login.</summary>
    public async Task<bool> CredentialsWorkAsync(CameraSettings camera, string userName, string password, CancellationToken token) {
        var result = camera.Kind switch {
            CameraSourceKind.Snapshot => await SnapshotAsync(camera.Address, userName, password, token).ConfigureAwait(false),
            CameraSourceKind.Rtsp => await RtspFrameAsync(camera.Address, userName, password, camera.Width, token).ConfigureAwait(false),
            _ => GrabResult.Fail("Folders have no camera login"),
        };
        return result.IsSuccess;
    }

    // MARK: Snapshot address

    private async Task<GrabResult> SnapshotAsync(string address, string userName, string? password, CancellationToken token) {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) {
            return GrabResult.Fail("The snapshot address must start with http:// or https://");
        }
        var handler = handlerFactory?.Invoke() ?? new HttpClientHandler {
            // Basic or Digest, whichever the camera asks for.
            Credentials = string.IsNullOrEmpty(userName) ? null : new NetworkCredential(userName, password ?? string.Empty),
            PreAuthenticate = false,
            // Cameras on the local network often have self-signed certificates.
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        using var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(12) };
        try {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) {
                return GrabResult.Fail("The camera refused the username or password");
            }
            if (!response.IsSuccessStatusCode) {
                return GrabResult.Fail($"The camera answered HTTP {(int)response.StatusCode}");
            }
            var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) {
                return GrabResult.Fail($"The address returned {(type.Length > 0 ? type : "no picture")}, not a picture");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0) {
                memory.Write(buffer, 0, read);
                if (memory.Length > MaxDownloadBytes) {
                    return GrabResult.Fail("The picture is too large");
                }
            }
            return memory.Length > 0 ? GrabResult.Ok(memory.ToArray()) : GrabResult.Fail("The camera sent an empty picture");
        } catch (OperationCanceledException) when (!token.IsCancellationRequested) {
            return GrabResult.Fail("The camera didn't answer in time");
        } catch (HttpRequestException ex) {
            return GrabResult.Fail($"Couldn't reach the camera: {ex.Message}");
        }
    }

    // MARK: Folder

    /// <summary>How deep date folders go (2026\10\15\hour is four levels) and how many of the newest subfolders are
    /// opened at each level (a sibling like "keograms" can be written after the day's folder).</summary>
    private const int FolderDepth = 4;
    private const int FoldersPerLevel = 2;

    /// <summary>
    /// The newest picture in the folder or the date folders under it (a new folder each day, or year\month\day), if it
    /// was written recently (otherwise the program writing it has stopped). Only the most recently written subfolders
    /// are opened at each level, so a years-deep archive costs a handful of directory listings, not a full scan.
    /// </summary>
    public static GrabResult NewestInFolder(string folder, DateTimeOffset now) {
        try {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) {
                return GrabResult.Fail("The folder doesn't exist");
            }
            var newest = NewestPicture(new DirectoryInfo(folder), FolderDepth);
            if (newest is null) {
                return GrabResult.Fail("No pictures in the folder yet");
            }
            if (now.UtcDateTime - newest.LastWriteTimeUtc > CameraPolicy.FolderMaxAge) {
                return GrabResult.Fail($"The newest picture is from {newest.LastWriteTime:HH:mm}: whatever saves them has stopped");
            }
            if (newest.Length > MaxDownloadBytes) {
                return GrabResult.Fail("The picture is too large");
            }
            // The writer may still be saving it: read shared, and retry once if it was mid-write.
            for (var attempt = 0; attempt < 2; attempt++) {
                try {
                    using var file = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var bytes = new byte[file.Length];
                    file.ReadExactly(bytes);
                    if (bytes.Length > 0) {
                        return GrabResult.Ok(bytes);
                    }
                } catch (IOException) when (attempt == 0) {
                    Thread.Sleep(500);
                }
            }
            return GrabResult.Fail("The newest picture couldn't be read");
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return GrabResult.Fail($"The folder couldn't be read: {ex.Message}");
        }
    }

    private static FileInfo? NewestPicture(DirectoryInfo dir, int depth) {
        var best = FolderPatterns
            .SelectMany(p => dir.EnumerateFiles(p, SearchOption.TopDirectoryOnly))
            .MaxBy(f => f.LastWriteTimeUtc);
        if (depth == 0) {
            return best;
        }
        // A folder's time changes when something is added to it, so today's folder (and its year and month) come first;
        // the name breaks ties, which suits 2026-10-15 style names.
        var subfolders = dir.EnumerateDirectories()
            .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .ThenByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Take(FoldersPerLevel);
        foreach (var sub in subfolders) {
            FileInfo? found;
            try {
                found = NewestPicture(sub, depth - 1);
            } catch (UnauthorizedAccessException) {
                continue;
            }
            if (found is not null && (best is null || found.LastWriteTimeUtc > best.LastWriteTimeUtc)) {
                best = found;
            }
        }
        return best;
    }

    // MARK: RTSP (one frame, decoded in memory by the host)

    private Task<GrabResult> RtspFrameAsync(string address, string userName, string? password, int width, CancellationToken token) {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || (uri.Scheme != "rtsp" && uri.Scheme != "rtsps")) {
            return Task.FromResult(GrabResult.Fail("The RTSP address must start with rtsp://"));
        }
        if (!string.IsNullOrEmpty(uri.UserInfo)) {
            return Task.FromResult(GrabResult.Fail("Put the login in the username and password boxes, not in the address"));
        }
        return rtsp is null
            ? Task.FromResult(GrabResult.Fail("RTSP isn't available in this build"))
            : rtsp(address, userName, password, width, token);
    }
}

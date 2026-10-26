using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NightManager.Core.Contracts;
using NightManager.Core.Security;

namespace NightManager.Core.Sync;

public sealed record ApiResult(int StatusCode, string? Body, TimeSpan? RetryAfter = null, string? Error = null) {
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    public bool IsUnauthorized => StatusCode is 401 or 403;

    /// <summary>Network failures (status 0), throttling and server errors are worth retrying.</summary>
    public bool IsTransient => StatusCode is 0 or 408 or 425 or 429 or >= 500;
}

public interface IApiTransport {
    Task<ApiResult> PostJsonAsync(string path, string json, ApiCredential? credential, CancellationToken token);

    Task<ApiResult> UploadBlobAsync(BlobUpload upload, string filePath, string contentType, CancellationToken token);
}

/// <summary>HTTPS transport. Signs requests when a credential is supplied.</summary>
public sealed class HttpApiTransport : IApiTransport, IDisposable {
    private readonly HttpClient client;
    private readonly Uri baseUri;
    private readonly TimeProvider time;

    public HttpApiTransport(Uri baseUri, string userAgent, TimeProvider? time = null, HttpMessageHandler? handler = null) {
        this.baseUri = baseUri;
        this.time = time ?? TimeProvider.System;
        client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    public async Task<ApiResult> PostJsonAsync(string path, string json, ApiCredential? credential, CancellationToken token) {
        var body = Encoding.UTF8.GetBytes(json);
        using var request = new HttpRequestMessage(HttpMethod.Post, Combine(path)) {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        if (credential is not null) {
            // Sign the path as the server sees it (base path + endpoint).
            var signed = RequestSigner.Sign(credential, "POST", request.RequestUri!.AbsolutePath, body, time.GetUtcNow());
            request.Headers.Add(RequestSigner.KeyHeader, signed.KeyId);
            request.Headers.Add(RequestSigner.TimestampHeader, signed.Timestamp);
            request.Headers.Add(RequestSigner.SignatureHeader, signed.Signature);
        }

        return await SendAsync(request, token).ConfigureAwait(false);
    }

    public async Task<ApiResult> UploadBlobAsync(BlobUpload upload, string filePath, string contentType, CancellationToken token) {
        await using var stream = File.OpenRead(filePath);
        using var request = new HttpRequestMessage(new HttpMethod(upload.Method), upload.Url) {
            Content = new StreamContent(stream)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        foreach (var (name, value) in upload.Headers) {
            // The signed URL covers the exact Content-Type: never send it twice ("image/jpeg, image/jpeg").
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase)) {
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
                continue;
            }
            if (!request.Headers.TryAddWithoutValidation(name, value)) {
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return await SendAsync(request, token).ConfigureAwait(false);
    }

    private Uri Combine(string path) => new(baseUri.AbsoluteUri.TrimEnd('/') + path);

    private async Task<ApiResult> SendAsync(HttpRequestMessage request, CancellationToken token) {
        try {
            using var response = await client.SendAsync(request, token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var retryAfter = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } date ? date - time.GetUtcNow() : null);

            return new ApiResult((int)response.StatusCode, body, retryAfter);
        } catch (OperationCanceledException) when (token.IsCancellationRequested) {
            throw;
        } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) {
            // Timeouts surface as TaskCanceledException without the caller's token being cancelled.
            return new ApiResult(0, null, null, ex.Message);
        }
    }

    public void Dispose() => client.Dispose();
}

internal static class StatusCodes {
    public static bool IsBadRequest(int status) => status is (int)HttpStatusCode.BadRequest or (int)HttpStatusCode.UnprocessableEntity or (int)HttpStatusCode.RequestEntityTooLarge;
}

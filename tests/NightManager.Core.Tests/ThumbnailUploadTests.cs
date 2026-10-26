using NightManager.Core.Contracts;
using NightManager.Core.Sync;
using System.Net;

namespace NightManager.Core.Tests;

public class ThumbnailUploadTests {
    private sealed class Capture : HttpMessageHandler {
        public string? ContentType;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
            ContentType = request.Content?.Headers.TryGetValues("Content-Type", out var v) == true ? string.Join(", ", v) : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    /// <summary>
    /// The signed URL covers the exact Content-Type. The backend also lists it in the upload's headers; sending it
    /// twice ("image/jpeg, image/jpeg") broke the signature, so Google Storage refused every thumbnail.
    /// </summary>
    [Test]
    public async Task Sends_the_content_type_exactly_once() {
        var handler = new Capture();
        using var transport = new HttpApiTransport(new Uri("https://example.test"), "test", handler: handler);
        var file = Path.GetTempFileName();
        try {
            await File.WriteAllBytesAsync(file, [1, 2, 3]);
            var upload = new BlobUpload { ItemId = "x", Url = "https://storage.example.test/put", Headers = new Dictionary<string, string> { ["Content-Type"] = "image/jpeg" } };
            var result = await transport.UploadBlobAsync(upload, file, "image/jpeg", CancellationToken.None);
            Assert.That(result.IsSuccess);
            Assert.That(handler.ContentType, Is.EqualTo("image/jpeg"));
        } finally {
            File.Delete(file);
        }
    }
}

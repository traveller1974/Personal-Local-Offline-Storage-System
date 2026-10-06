using System.Net;
using System.Text.Json;
using Stock.Recognition;
using Xunit;

namespace Stock.Recognition.Tests;

public sealed class DiagnosticTests : IDisposable
{
    private readonly string image = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
    public DiagnosticTests() => File.WriteAllBytes(image, [1, 2, 3]);
    private sealed class Handler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
    private static string Envelope(string text, string finish = "stop") => JsonSerializer.Serialize(new
    {
        output = new { choices = new[] { new { finish_reason = finish, message = new { content = new[] { new { text } } } } } },
        usage = new { input_tokens = 15, output_tokens = 12 }
    });
    [Fact] public async Task ModelParseFailureKeepsOriginalEnvelopeTextUsageAndStage()
    {
        var body = Envelope("{\"rows\":[}");
        var handler = new Handler(body);
        using var client = new HttpClient(handler);
        var service = new QwenRecognitionService(client, new("unit-test-key", QwenConfiguration.Beijing));
        await Assert.ThrowsAsync<RecognitionException>(() => service.RecognizeAsync(image));
        var capture = Assert.IsType<RecognitionDiagnostic>(service.LastDiagnostic);
        Assert.Equal(body, capture.RawResponse); Assert.Equal("{\"rows\":[}", capture.ModelText);
        Assert.Equal(15, capture.InputTokens); Assert.Equal("ModelParse", capture.Stage); Assert.Null(capture.Result);
        Assert.NotEmpty(capture.ImageSha256); Assert.NotEmpty(capture.Error!); Assert.Equal(1, handler.Calls);
    }
    [Fact] public async Task TruncatedOutputIsStillAvailableForInspection()
    {
        using var client = new HttpClient(new Handler(Envelope("unfinished JSON", "length")));
        var service = new QwenRecognitionService(client, new("unit-test-key", QwenConfiguration.Beijing));
        await Assert.ThrowsAsync<RecognitionException>(() => service.RecognizeAsync(image));
        Assert.Equal("unfinished JSON", service.LastDiagnostic!.ModelText);
        Assert.Equal("Envelope", service.LastDiagnostic.Stage);
    }
    [Fact] public async Task ErrorResponseIsCapturedAndSecretsOrImagePayloadAreRemoved()
    {
        using var client = new HttpClient(new Handler("{\"code\":\"InvalidApiKey\",\"message\":\"unit-test-key data:image/png;base64,AQID\"}", HttpStatusCode.Unauthorized));
        var service = new QwenRecognitionService(client, new("unit-test-key", QwenConfiguration.Beijing));
        await Assert.ThrowsAsync<RecognitionException>(() => service.RecognizeAsync(image));
        var json = JsonSerializer.Serialize(service.LastDiagnostic, RecognitionJson.Options);
        Assert.DoesNotContain("unit-test-key", json); Assert.DoesNotContain("AQID", json);
        Assert.Equal(401, service.LastDiagnostic!.HttpStatus); Assert.Equal("HttpError", service.LastDiagnostic.Stage);
    }
    [Fact] public async Task RecorderFailureDoesNotChangeResult()
    {
        using var client = new HttpClient(new Handler(Envelope(QwenTests.Sample().ToJsonString())));
        var service = new QwenRecognitionService(client, new("unit-test-key", QwenConfiguration.Beijing))
        { DiagnosticSink = _ => throw new IOException("test recorder error") };
        var result = await service.RecognizeAsync(image);
        Assert.Equal(3, result.Rows.Count); Assert.Equal(result, service.LastDiagnostic!.Result);
    }
    [Fact] public async Task OversizedOrEmptyImageNeverReachesHttpHandler()
    {
        var handler = new Handler("{}");
        using var client = new HttpClient(handler);
        var service = new QwenRecognitionService(client, new("unit-test-key", QwenConfiguration.Beijing));
        foreach (var bytes in new[] { Array.Empty<byte>(), new byte[7 * 1024 * 1024 + 1] })
        {
            File.WriteAllBytes(image, bytes);
            await Assert.ThrowsAsync<RecognitionException>(() => service.RecognizeAsync(image));
            Assert.Equal("ImageRead", service.LastDiagnostic!.Stage);
        }
        Assert.Equal(0, handler.Calls);
    }
    public void Dispose() => File.Delete(image);
}

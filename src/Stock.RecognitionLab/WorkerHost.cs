using System.IO;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Text.Json;
using Stock.Recognition;

namespace Stock.RecognitionLab;

internal static class WorkerHost
{
    internal static async Task<int> RunAsync(string? replayFile = null)
    {
        RecognitionWorkerRequest? request = null;
        RecognitionWorkerResponse reply;
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        try
        {
            var line = await input.ReadLineAsync() ?? throw new RecognitionException("识别模块未收到请求。");
            request = JsonSerializer.Deserialize<RecognitionWorkerRequest>(line, RecognitionWorkerProtocol.Json)
                ?? throw new RecognitionException("识别模块请求格式无效。");
            if (request.ProtocolVersion != RecognitionWorkerProtocol.Version || !Guid.TryParse(request.RequestId, out _))
                throw new RecognitionException("识别模块协议版本或请求编号无效。");
            if (request.Operation is not (RecognitionWorkerProtocol.Recognize or RecognitionWorkerProtocol.Locate))
                throw new RecognitionException("识别模块操作无效。");
            if (request.Configuration is null) throw new RecognitionException("识别模块缺少配置。");
            var configuration = request.ProviderConfiguration ?? RecognitionConfiguration.From(request.Configuration);
            configuration.Validate();
            using var client = replayFile is null
                ? new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan }
                : ReplayClient(replayFile, request.Operation);
            var service = RecognitionProviderFactory.Create(client, configuration, request.Context);
            reply = request.Operation == RecognitionWorkerProtocol.Recognize
                ? new(request.RequestId, true, Result: await service.RecognizeAsync(request.ConfirmedImage))
                : new(request.RequestId, true, Regions: await service.LocateAsync(request.ConfirmedImage));
        }
        catch (Exception ex)
        {
            var message = ex is RecognitionException ? ex.Message : "识别模块无法处理本次请求，图片与核对明细已保留。";
            var key = request?.ProviderConfiguration?.ApiKey ?? request?.Configuration?.ApiKey;
            if (!string.IsNullOrEmpty(key)) message = message.Replace(key, "[REDACTED]", StringComparison.Ordinal);
            reply = new(request?.RequestId ?? "", false, Error: message);
        }
        await output.WriteLineAsync(JsonSerializer.Serialize(reply, RecognitionWorkerProtocol.Json));
        return reply.Success ? 0 : 1;
    }
    private static HttpClient ReplayClient(string path, string operation)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(path));
        var response = fixture.RootElement.GetProperty(operation).GetString() ?? "";
        var delay = fixture.RootElement.TryGetProperty("delayMilliseconds", out var d) ? d.GetInt32() : 0;
        var started = fixture.RootElement.TryGetProperty("startedFile", out var s) ? s.GetString() : null;
        var status = fixture.RootElement.TryGetProperty("statusCode", out var c) ? (HttpStatusCode)c.GetInt32() : HttpStatusCode.OK;
        return new HttpClient(new ReplayHandler(response, delay, started, status)) { Timeout = Timeout.InfiniteTimeSpan };
    }
    // Only the explicit offline replay entry point uses these fixture controls. Production never loads a fixture.
    private sealed class ReplayHandler(string response, int delay, string? started, HttpStatusCode status) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (started is not null) await File.WriteAllTextAsync(started, Environment.ProcessId.ToString(), cancellationToken);
            if (delay > 0) await Task.Delay(delay, cancellationToken);
            return new(status) { Content = new StringContent(response) };
        }
    }
}

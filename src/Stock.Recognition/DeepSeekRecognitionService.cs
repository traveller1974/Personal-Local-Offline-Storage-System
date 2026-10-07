using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Stock.Recognition;

/// <summary>Official DeepSeek vision API. One bounded request, no automatic provider switching.</summary>
public sealed class DeepSeekRecognitionService(HttpClient client, RecognitionConfiguration configuration) : IRecognitionProvider
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(90);
    public RecognitionContext Context { get; init; } = new();
    public RecognitionDiagnostic? LastDiagnostic { get; private set; }
    public Task<IReadOnlyList<TextRegion>> LocateAsync(string confirmedImage, CancellationToken cancellationToken = default) =>
        throw new RecognitionException("DeepSeek 暂不提供文字坐标，请用左侧图片缩放对照。");

    public async Task<RecognitionResult> RecognizeAsync(string confirmedImage, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow; var watch = Stopwatch.StartNew();
        var hash = ""; var raw = ""; var text = ""; var stage = "Validation"; string? error = null;
        int? status = null; long? input = null, output = null; RecognitionResult? result = null;
        var prompt = Context.Prompt;
        try
        {
            var endpoint = configuration.Validate();
            stage = "ImageRead";
            var content = new List<object>();
            var paths = new[] { confirmedImage }.Concat(Context.DetailImages ?? []).Take(9).ToArray();
            foreach (var path in paths)
            {
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                if (path == confirmedImage) hash = Convert.ToHexString(SHA256.HashData(bytes));
                var extension = Path.GetExtension(path);
                if (bytes.Length == 0 || bytes.Length > 7 * 1024 * 1024 || !new[] { ".png", ".jpg", ".jpeg" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    throw new RecognitionException("请使用7MB以内的 JPG 或 PNG 表格截图。未发送请求。");
                var mime = extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
                content.Add(new { type = "image_url", image_url = new { url = $"data:{mime};base64,{Convert.ToBase64String(bytes)}" } });
            }
            if (paths.Length > 1)
                prompt += "\n第一张为整单，后续图片为同一单据自上向下的细节片段。以细节为准，参考整单表头和分区。重复展示的表头、交界区域只提取一次；不同位置出现的同名货品必须保留。按整单原位置输出，禁止按名称去重。";
            content.Add(new { type = "text", text = prompt });
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.ApiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = RecognitionConfiguration.DeepSeekModel,
                messages = new[] { new { role = "user", content } },
                response_format = new { type = "json_object" }, max_tokens = 16384,
                thinking = new { type = "disabled" }
            }), Encoding.UTF8, "application/json");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Timeout); stage = "Transport";
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            status = (int)response.StatusCode;
            raw = Redact(await response.Content.ReadAsStringAsync(deadline.Token));
            cancellationToken.ThrowIfCancellationRequested();
            if (!response.IsSuccessStatusCode)
            {
                stage = "HttpError";
                throw new RecognitionException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "DeepSeek 密钥无效，请在设置中重新填写。",
                    HttpStatusCode.PaymentRequired => "DeepSeek 余额不足，请充值后主动重试。",
                    HttpStatusCode.Forbidden => "DeepSeek 服务权限不足，请检查账户。",
                    HttpStatusCode.TooManyRequests => "DeepSeek 请求较多，请稍后主动重试。",
                    _ => "DeepSeek 暂时无法识别，请检查服务后主动重试。"
                });
            }
            stage = "Envelope";
            using var document = JsonDocument.Parse(raw); var root = document.RootElement;
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var i) && i.TryGetInt64(out var iv)) input = iv;
                if (usage.TryGetProperty("completion_tokens", out var o) && o.TryGetInt64(out var ov)) output = ov;
            }
            var choices = root.GetProperty("choices");
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1 || choices[0].GetProperty("finish_reason").GetString() != "stop")
                throw new RecognitionException("识别输出没有完整结束，请缩小表格范围后重试。");
            text = choices[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(text)) throw new RecognitionException("DeepSeek 未返回识别内容，请主动重试或手动填写。");
            stage = "ModelParse";
            result = RecognitionParser.Parse(text) with { Elapsed = watch.Elapsed, InputTokens = input, OutputTokens = output, Provider = "DeepSeek", PromptVersion = QwenRecognitionService.PromptVersion };
            cancellationToken.ThrowIfCancellationRequested(); stage = "Completed"; return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { stage = "Timeout"; error = $"DeepSeek 识别超过{Timeout.TotalSeconds:g}秒，已停止等待。"; throw new RecognitionException(error); }
        catch (OperationCanceledException) { stage = "Cancelled"; error = "识别已取消。"; throw; }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { error = "DeepSeek 返回的数据格式不完整，请主动重试或手动填写。"; throw new RecognitionException(error); }
        catch (HttpRequestException) { error = "无法连接 DeepSeek，图片与填写内容已保留。"; throw new RecognitionException(error); }
        catch (Exception ex) { error = Redact(ex.Message); throw; }
        finally
        {
            watch.Stop();
            LastDiagnostic = new(started, "Recognize", RecognitionConfiguration.DeepSeekModel, QwenRecognitionService.PromptVersion,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))), configuration.Endpoint, hash, watch.Elapsed, status,
                raw, text, input, output, stage, error, result, null)
                { CorrectionExampleCount = Math.Min(3,Context.Examples?.Count??0), DetailImageCount = Math.Min(8,Context.DetailImages?.Count??0) };
        }
    }
    private string Redact(string value)
    {
        if (configuration.ApiKey.Length > 0) value = value.Replace(configuration.ApiKey, "[REDACTED]", StringComparison.Ordinal);
        return Regex.Replace(value, @"data:image/[a-zA-Z0-9.+-]+;base64,[A-Za-z0-9+/=\r\n]+", "[IMAGE REDACTED]");
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Stock.Recognition;

public sealed partial class QwenRecognitionService(HttpClient client, QwenConfiguration configuration) : IRecognitionProvider
{
    public const string Model = "qwen3.5-ocr";
    public const string PromptVersion = "stock-invoice-v1.2.1";
    public static string PromptSha256 => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Prompt)));
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(90);
    public RecognitionDiagnostic? LastDiagnostic { get; private set; }
    public Action<RecognitionDiagnostic>? DiagnosticSink { get; init; }
    public RecognitionContext Context { get; init; } = new();

    private sealed class Call(bool locate, string endpoint)
    {
        public readonly bool Locate = locate;
        public readonly string Endpoint = endpoint;
        public readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
        public readonly Stopwatch Watch = Stopwatch.StartNew();
        public string ImageHash = "", Raw = "", Text = "", Stage = "Validation";
        public int? Status;
        public long? Input, Output;
        public RecognitionResult? Result;
        public IReadOnlyList<TextRegion>? Regions;
        public string? Error;
    }

    private string Redact(string value)
    {
        if (!string.IsNullOrEmpty(configuration.ApiKey)) value = value.Replace(configuration.ApiKey, "[REDACTED]", StringComparison.Ordinal);
        return Regex.Replace(value, @"data:image/[a-zA-Z0-9.+-]+;base64,[A-Za-z0-9+/=\r\n]+", "[IMAGE REDACTED]");
    }

    private void Publish(Call call)
    {
        call.Watch.Stop();
        var diagnostic = new RecognitionDiagnostic(call.StartedAt, call.Locate ? "Locate" : "Recognize", Model,
            PromptVersion, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Context.Prompt))), Redact(call.Endpoint), call.ImageHash, call.Watch.Elapsed, call.Status,
            call.Raw, call.Text, call.Input, call.Output, call.Stage, call.Error, call.Result, call.Regions)
            { CorrectionExampleCount = Math.Min(3,Context.Examples?.Count??0) };
        LastDiagnostic = diagnostic;
        // A caller's optional recorder must not turn a successful recognition into a failure.
        try { DiagnosticSink?.Invoke(diagnostic); } catch { }
    }

    private async Task<JsonDocument> Request(string path, Call call, CancellationToken token)
    {
        var endpoint = configuration.Validate();
        call.Stage = "ImageRead";
        var bytes = await File.ReadAllBytesAsync(path, token);
        call.ImageHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (bytes.Length == 0 || bytes.Length > 7 * 1024 * 1024 || 4L * ((bytes.Length + 2L) / 3) > 10 * 1024 * 1024)
            throw new RecognitionException("确认截图为空、原文件超过7MB或 Base64 编码超过10MB，请重新裁剪。未发送请求。");
        var extension = Path.GetExtension(path);
        if (!new[] { ".png", ".jpg", ".jpeg" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new RecognitionException("请选择 JPG 或 PNG 截图。未发送请求。");
        var mime = extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
        var content = new List<object> { new { image = $"data:{mime};base64,{Convert.ToBase64String(bytes)}", min_pixels = 3072, max_pixels = 15680000, enable_rotate = false } };
        if (!call.Locate) content.Add(new { text = Context.Prompt });
        var body = new
        {
            model = Model, input = new { messages = new[] { new { role = "user", content } } },
            parameters = call.Locate ? (object)new { ocr_options = new { task = "advanced_recognition" } } : new { max_tokens = 16384 }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.ApiKey);
        if (configuration.Workspace.Length > 0) request.Headers.Add("X-DashScope-WorkSpace", configuration.Workspace);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(Timeout);
        call.Stage = "Transport";
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            call.Status = (int)response.StatusCode;
            call.Raw = Redact(await response.Content.ReadAsStringAsync(deadline.Token));
            token.ThrowIfCancellationRequested();
            if (!response.IsSuccessStatusCode)
            {
                call.Stage = "HttpError";
                var code = "";
                try
                {
                    using var error = JsonDocument.Parse(call.Raw);
                    if (error.RootElement.ValueKind == JsonValueKind.Object && error.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                        code = c.GetString() ?? "";
                }
                catch (JsonException) { }
                var message = code.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) ? "百炼鉴权失败，请核对 API Key 和地域。" :
                    new[] { "Balance", "Arrear", "Overdue", "Insufficient" }.Any(c => code.Contains(c, StringComparison.OrdinalIgnoreCase)) ? "百炼余额不足或服务欠费。" : response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized => "百炼鉴权失败，请核对 API Key 和地域。",
                        HttpStatusCode.PaymentRequired => "百炼余额不足或服务欠费。",
                        HttpStatusCode.Forbidden => "百炼没有模型或业务空间权限，请核对控制台。",
                        HttpStatusCode.TooManyRequests => "百炼请求限流，请稍后主动重试。",
                        _ => "百炼识别请求失败，请检查服务配置后主动重试。"
                    };
                throw new RecognitionException(message + " 图片已保留；没有自动重试。");
            }
            call.Stage = "Envelope";
            return JsonDocument.Parse(call.Raw);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            call.Stage = "Timeout";
            throw new RecognitionException($"百炼识别超过{Timeout.TotalSeconds:0.###}秒，已停止等待；请主动重试。");
        }
        catch (HttpRequestException)
        {
            throw new RecognitionException("无法连接百炼服务。图片已保留，请检查网络后主动重试。");
        }
        catch (JsonException) { throw new RecognitionException("百炼返回了无效 JSON。"); }
    }

    private static JsonElement Content(JsonDocument document, Call call)
    {
        var root = document.RootElement;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("input_tokens", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt64(out var iv)) call.Input = iv;
            if (usage.TryGetProperty("output_tokens", out var o) && o.ValueKind == JsonValueKind.Number && o.TryGetInt64(out var ov)) call.Output = ov;
        }
        if (!root.TryGetProperty("output", out var output) || !output.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1)
            throw new RecognitionException("百炼响应结构无效。");
        var choice = choices[0];
        var content = choice.GetProperty("message").GetProperty("content");
        call.Text = string.Join("\n", content.EnumerateArray().Where(c => c.TryGetProperty("text", out _))
            .Select(c => c.GetProperty("text").GetString()));
        if (!choice.TryGetProperty("finish_reason", out var finish) || finish.GetString() != "stop")
            throw new RecognitionException("识别输出截断或未正常结束，请缩小表格范围后主动重试。");
        return content;
    }

    public async Task<RecognitionResult> RecognizeAsync(string confirmedImage, CancellationToken cancellationToken = default)
    {
        var call = new Call(false, configuration.Endpoint);
        try
        {
            using var document = await Request(confirmedImage, call, cancellationToken);
            var content = Content(document, call);
            var texts = content.EnumerateArray().Where(c => c.TryGetProperty("text", out _)).ToList();
            if (texts.Count != 1 || texts[0].GetProperty("text").ValueKind != JsonValueKind.String)
                throw new RecognitionException("识别结果没有完整结构化文字。");
            call.Stage = "ModelParse";
            var parsed = RecognitionParser.Parse(call.Text);
            cancellationToken.ThrowIfCancellationRequested();
            call.Result = parsed with { Elapsed = call.Watch.Elapsed, InputTokens = call.Input, OutputTokens = call.Output, Provider = "Qwen", PromptVersion = PromptVersion };
            call.Stage = "Completed";
            return call.Result;
        }
        catch (OperationCanceledException) { call.Stage = "Cancelled"; call.Error = "识别已取消。"; throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            call.Error = "百炼响应结构无效。";
            throw new RecognitionException(call.Error);
        }
        catch (Exception ex) { call.Error = Redact(ex.Message); throw; }
        finally { Publish(call); }
    }

    public async Task<IReadOnlyList<TextRegion>> LocateAsync(string confirmedImage, CancellationToken cancellationToken = default)
    {
        var call = new Call(true, configuration.Endpoint);
        try
        {
            using var document = await Request(confirmedImage, call, cancellationToken);
            var content = Content(document, call);
            call.Stage = "LocateParse";
            var regions = new List<TextRegion>();
            foreach (var entry in content.EnumerateArray())
                if (entry.TryGetProperty("ocr_result", out var ocr) && ocr.TryGetProperty("words_info", out var words))
                    foreach (var word in words.EnumerateArray())
                    {
                        var rect = word.GetProperty("rotate_rect").EnumerateArray().Select(p => p.GetDouble()).ToArray();
                        if (rect.Length != 5 || rect.Any(p => !double.IsFinite(p)) || rect[2] <= 0 || rect[3] <= 0)
                            throw new RecognitionException("文字定位坐标无效。");
                        regions.Add(new(word.GetProperty("text").GetString() ?? "", rect[0], rect[1], rect[2], rect[3], rect[4]));
                    }
            cancellationToken.ThrowIfCancellationRequested();
            call.Regions = regions;
            call.Stage = "Completed";
            return regions;
        }
        catch (OperationCanceledException) { call.Stage = "Cancelled"; call.Error = "文字定位已取消。"; throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            call.Error = "文字定位响应无效。";
            throw new RecognitionException(call.Error);
        }
        catch (Exception ex) { call.Error = Redact(ex.Message); throw; }
        finally { Publish(call); }
    }
}

using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Stock.Recognition;

/// <summary>Hidden one-shot worker. Never retries a request or starts a second provider after a failure.</summary>
public sealed class RecognitionWorkerClient(string executable, QwenConfiguration configuration) : IRecognitionService
{
    public RecognitionConfiguration? ProviderConfiguration { get; init; }
    public RecognitionContext? Context { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(210);
    // Explicit local developer replay; production leaves this unset and always uses --recognition-worker.
    public string? ReplayFile { get; init; }
    public async Task<RecognitionResult> RecognizeAsync(string confirmedImage, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(RecognitionWorkerProtocol.Recognize, confirmedImage, cancellationToken);
        return response.Result ?? throw new RecognitionException("识别模块没有返回完整结果。");
    }
    public async Task<IReadOnlyList<TextRegion>> LocateAsync(string confirmedImage, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(RecognitionWorkerProtocol.Locate, confirmedImage, cancellationToken);
        return response.Regions ?? throw new RecognitionException("识别模块没有返回文字定位结果。");
    }
    private async Task<RecognitionWorkerResponse> SendAsync(string operation, string image, CancellationToken cancellationToken)
    {
        (ProviderConfiguration ?? RecognitionConfiguration.From(configuration)).Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(executable)) throw new RecognitionException("内置识别模块缺失，请修复安装或切回原识别版本。");
        var id = Guid.NewGuid().ToString();
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false), WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        };
        if (ReplayFile is null) start.ArgumentList.Add("--recognition-worker");
        else { start.ArgumentList.Add("--worker-replay"); start.ArgumentList.Add(Path.GetFullPath(ReplayFile)); }
        using var process = new Process { StartInfo = start };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        try
        {
            if (!process.Start()) throw new RecognitionException("内置识别模块无法启动。");
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            var request = new RecognitionWorkerRequest(id, operation, Path.GetFullPath(image), configuration, ProviderConfiguration: ProviderConfiguration, Context: Context);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, RecognitionWorkerProtocol.Json).AsMemory(), deadline.Token);
            process.StandardInput.Close();
            var line = await process.StandardOutput.ReadLineAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            await stderr;
            cancellationToken.ThrowIfCancellationRequested();
            if (line is null) throw new RecognitionException("内置识别模块意外退出。没有自动重试，图片和明细已保留。");
            var response = JsonSerializer.Deserialize<RecognitionWorkerResponse>(line, RecognitionWorkerProtocol.Json)
                ?? throw new RecognitionException("内置识别模块响应无效。");
            if (response.RequestId != id || response.ProtocolVersion != RecognitionWorkerProtocol.Version)
                throw new RecognitionException("内置识别模块请求编号或协议版本不匹配。");
            if (!response.Success) throw new RecognitionException(response.Error ?? "内置识别模块请求失败。");
            if (process.ExitCode != 0) throw new RecognitionException("内置识别模块未正常结束。");
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new RecognitionException($"内置识别超过{Timeout.TotalSeconds:g}秒，已停止等待；没有自动重试，请主动重试。"); }
        catch (Exception ex) when (ex is JsonException or IOException or System.ComponentModel.Win32Exception)
        { throw new RecognitionException("内置识别模块启动或通信失败。没有自动重试，图片和明细已保留。"); }
        finally
        {
            try { if (process.Id > 0 && !process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
            catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        }
    }
}

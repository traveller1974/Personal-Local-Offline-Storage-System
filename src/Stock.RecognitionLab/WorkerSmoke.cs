using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Stock.Recognition;

namespace Stock.RecognitionLab;

/// <summary>Real child-process checks of the same single EXE used for standalone testing and embedding.</summary>
internal static class WorkerSmoke
{
    internal static async Task RunAsync(string output, Action<bool, string> check)
    {
        var executable = Environment.ProcessPath ?? throw new Exception("Missing executable path");
        var image = Path.Combine(output, "offline-ui-test.png");
        var fixture = Path.Combine(output, "worker-fixture.json");
        var raw = LabSmoke.ModelResponse();
        var locate = """{"output":{"choices":[{"finish_reason":"stop","message":{"content":[{"ocr_result":{"words_info":[{"text":"测试成车（123）","rotate_rect":[200,150,120,24,12]}]}}]}}]}}""";
        await File.WriteAllTextAsync(fixture, JsonSerializer.Serialize(new { recognize = raw, locate }, RecognitionJson.Options));
        var configuration = new QwenConfiguration("offline-pipe-secret", QwenConfiguration.Beijing);
        var client = new RecognitionWorkerClient(executable, configuration) { ReplayFile = fixture };
        var result = await client.RecognizeAsync(image);
        check(result.Rows.Count == 3 && result.Rows[0].OriginalOrder == "1" && result.Rows[2].RawQuantity == "0",
            "Single EXE worker returns original order, duplicate rows and zero quantity through UTF-8 pipes");
        check(result.Rows[0].Spec.Contains('\n') && result.InputTokens == 123 && result.OutputTokens == 77 &&
            result.SectionTotals[RecognitionProductType.Vehicle] == 5 && result.ActualQuantityColumn,
            "Worker protocol preserves complete specifications, tokens, totals and actual-column flag");
        var regions = await client.LocateAsync(image);
        check(regions.Count == 1 && regions[0] == new TextRegion("测试成车（123）", 200, 150, 120, 24, 12),
            "Single EXE worker returns every location coordinate and angle");

        var request = new RecognitionWorkerRequest(Guid.NewGuid().ToString(), RecognitionWorkerProtocol.Recognize, image, configuration);
        using (var process = Start(executable, "--worker-replay", fixture))
        {
            await Task.Delay(200);
            process.Refresh();
            check(process.MainWindowHandle == IntPtr.Zero, "Worker entry point never opens the standalone test window");
            check(!string.Join(" ", process.StartInfo.ArgumentList).Contains(configuration.ApiKey), "API key is absent from child process arguments");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, RecognitionWorkerProtocol.Json));
            process.StandardInput.Close();
            var response = await process.StandardOutput.ReadToEndAsync();
            var errors = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var parsed = JsonSerializer.Deserialize<RecognitionWorkerResponse>(response, RecognitionWorkerProtocol.Json)!;
            check(parsed.RequestId == request.RequestId && parsed.ProtocolVersion == RecognitionWorkerProtocol.Version && parsed.Success && process.ExitCode == 0,
                "Worker output is one valid JSON response with matching request ID and protocol version");
            check(!response.Contains(configuration.ApiKey) && !errors.Contains(configuration.ApiKey), "Worker output and stderr exclude the pipe secret");
        }

        // Exercise the production entry point with a locally rejected endpoint; it must never contact the network.
        using (var process = Start(executable, "--recognition-worker"))
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request with
                { Configuration = configuration with { Endpoint = "http://127.0.0.1:1/forbidden" } }, RecognitionWorkerProtocol.Json));
            process.StandardInput.Close();
            var parsed = JsonSerializer.Deserialize<RecognitionWorkerResponse>(await process.StandardOutput.ReadToEndAsync(), RecognitionWorkerProtocol.Json)!;
            await process.WaitForExitAsync();
            check(!parsed.Success && parsed.Error!.Contains("官方") && process.ExitCode == 1,
                "Production worker rejects an invalid endpoint before making a cloud request");
        }
        foreach (var bad in new[] { request with { ProtocolVersion = 99 }, request with { Operation = "unexpected" } })
        {
            using var process = Start(executable, "--worker-replay", fixture);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(bad, RecognitionWorkerProtocol.Json));
            process.StandardInput.Close();
            var parsed = JsonSerializer.Deserialize<RecognitionWorkerResponse>(await process.StandardOutput.ReadToEndAsync(), RecognitionWorkerProtocol.Json)!;
            await process.WaitForExitAsync();
            check(!parsed.Success && parsed.RequestId == bad.RequestId, "Worker rejects unsupported protocol or operation without a request: " + bad.Operation + "/" + bad.ProtocolVersion);
        }

        var malformed = Path.Combine(output, "worker-malformed.json");
        await File.WriteAllTextAsync(malformed, JsonSerializer.Serialize(new { recognize = "{invalid", locate }));
        try
        {
            await new RecognitionWorkerClient(executable, configuration) { ReplayFile = malformed }.RecognizeAsync(image);
            throw new Exception("Malformed response must fail");
        }
        catch (RecognitionException ex) { check(ex.Message.Contains("JSON"), "Malformed model envelope becomes a controlled worker failure"); }
        try
        {
            await new RecognitionWorkerClient(Path.Combine(output, "missing-worker.exe"), configuration).RecognizeAsync(image);
            throw new Exception("Missing worker must fail");
        }
        catch (RecognitionException ex) { check(ex.Message.Contains("缺失"), "Missing worker is reported before starting a process"); }

        var signal = Path.Combine(output, "worker-started.txt");
        if (File.Exists(signal)) File.Delete(signal);
        var delayed = Path.Combine(output, "worker-delayed.json");
        await File.WriteAllTextAsync(delayed, JsonSerializer.Serialize(new { recognize = raw, locate, delayMilliseconds = 60000, startedFile = signal }));
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = new RecognitionWorkerClient(executable, configuration) { ReplayFile = delayed }.RecognizeAsync(image, cancellation.Token);
            await WaitForSignal(signal, pending);
            var pid = int.Parse(await File.ReadAllTextAsync(signal));
            cancellation.Cancel();
            try { await pending; throw new Exception("Cancelled call must fail"); }
            catch (OperationCanceledException) { check(Exited(pid), "Cancelling an in-flight worker kills it and discards late results"); }
        }
        File.Delete(signal);
        var watch = Stopwatch.StartNew();
        try
        {
            await new RecognitionWorkerClient(executable, configuration) { ReplayFile = delayed, Timeout = TimeSpan.FromSeconds(3) }.RecognizeAsync(image);
            throw new Exception("Worker deadline must fail");
        }
        catch (RecognitionException ex)
        {
            check(ex.Message.Contains("超过3秒") && watch.Elapsed < TimeSpan.FromSeconds(8), "Worker deadline includes startup and stops a stalled child process");
            check(File.Exists(signal) && Exited(int.Parse(await File.ReadAllTextAsync(signal))), "Timed-out worker process is reaped without a retry");
        }
    }

    private static Process Start(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new Exception("Worker failed to start");
    }
    private static async Task WaitForSignal(string path, Task pending)
    {
        var watch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (pending.IsCompleted) { await pending; throw new Exception("Worker finished before delay"); }
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new Exception("Worker never entered offline replay handler");
            await Task.Delay(30);
        }
        // WriteAllTextAsync can briefly expose an empty file before closing it.
        while (new FileInfo(path).Length == 0) await Task.Delay(10);
    }
    private static bool Exited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }
}

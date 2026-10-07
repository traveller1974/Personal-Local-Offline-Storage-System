using System.IO;
using System.Security.Cryptography;
using Stock.Core;
using Recognition = Stock.Recognition;

namespace Stock.Desktop;

/// <summary>Adapts the bundled worker to the existing review and purchase workflow.</summary>
internal sealed class EmbeddedRecognitionService(QwenConfiguration configuration, IRecognitionService original,
    Func<string>? resolveExecutable = null, string? replayFile = null) : IRecognitionService
{
    public Recognition.RecognitionConfiguration? ProviderConfiguration { get; init; }
    public Recognition.RecognitionContext? Context { get; init; }
    private async Task<Recognition.RecognitionWorkerClient?> PrepareAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            // Extracting and checking the self-contained executable must not block the WPF thread.
            var path = await Task.Run(resolveExecutable ?? (() => EmbeddedRecognitionPayload.Extract()), token);
            token.ThrowIfCancellationRequested();
            return new(path, new(configuration.ApiKey, configuration.Endpoint, configuration.Workspace)) { ReplayFile = replayFile, ProviderConfiguration = ProviderConfiguration, Context = Context };
        }
        // Only local preparation failures can use the original provider, before any request is started.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public async Task<RecognitionResult> RecognizeAsync(string confirmedImage, CancellationToken cancellationToken = default)
    {
        var worker = await PrepareAsync(cancellationToken);
        if (worker is null) return await original.RecognizeAsync(confirmedImage, cancellationToken);
        try { return Map(await worker.RecognizeAsync(confirmedImage, cancellationToken)); }
        catch (Recognition.RecognitionException ex) { throw new BusinessException(ex.Message); }
    }

    public async Task<IReadOnlyList<TextRegion>> LocateAsync(string confirmedImage, CancellationToken cancellationToken = default)
    {
        var worker = await PrepareAsync(cancellationToken);
        if (worker is null) return await original.LocateAsync(confirmedImage, cancellationToken);
        try
        {
            return (await worker.LocateAsync(confirmedImage, cancellationToken))
                .Select(r => new TextRegion(r.Text, r.CenterX, r.CenterY, r.Width, r.Height, r.Angle)).ToArray();
        }
        catch (Recognition.RecognitionException ex) { throw new BusinessException(ex.Message); }
    }

    internal static RecognitionResult Map(Recognition.RecognitionResult result) => RecognitionMapping.Map(result);

    private static ProductType MapType(Recognition.RecognitionProductType type) => type switch
    {
        Recognition.RecognitionProductType.Vehicle => ProductType.Vehicle,
        Recognition.RecognitionProductType.Battery => ProductType.Battery,
        Recognition.RecognitionProductType.Charger => ProductType.Charger,
        Recognition.RecognitionProductType.Accessory => ProductType.Accessory,
        _ => ProductType.Unknown
    };
}

internal static class EmbeddedRecognitionPayload
{
    internal const string ResourceName = "Stock.Desktop.Recognition.Stock.RecognitionLab.exe";
    private static readonly Lazy<string> Hash = new(() =>
    {
        using var stream = Open();
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    });
    internal static string Sha256 => Hash.Value;
    private static Stream Open() => typeof(EmbeddedRecognitionPayload).Assembly.GetManifestResourceStream(ResourceName)
        ?? throw new IOException("内置识别模块缺失，请修复安装。");

    internal static string Extract(string? cacheRoot = null)
    {
        cacheRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalStockManager", "RecognitionModules");
        var directory = Path.Combine(Path.GetFullPath(cacheRoot), Sha256);
        var executable = Path.Combine(directory, "Stock.RecognitionLab.exe");
        if (File.Exists(executable))
        {
            using var existing = File.OpenRead(executable);
            if (Convert.ToHexString(SHA256.HashData(existing)).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
                return executable;
        }
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var source = Open())
            using (var target = File.Create(temporary)) source.CopyTo(target);
            File.Move(temporary, executable, true);
            return executable;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

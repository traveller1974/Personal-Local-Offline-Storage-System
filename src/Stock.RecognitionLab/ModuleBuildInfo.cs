using System.IO;
using System.Security.Cryptography;

namespace Stock.RecognitionLab;

internal sealed record ModuleBuildInfo(string Executable, string Version, string Sha256)
{
    private static readonly Lazy<ModuleBuildInfo> Build = new(() =>
    {
        var path = Environment.ProcessPath ?? throw new IOException("无法确认当前测试程序版本。");
        using var stream = File.OpenRead(path);
        return new(Path.GetFileName(path), typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
            Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
    });
    internal static ModuleBuildInfo Current => Build.Value;
}

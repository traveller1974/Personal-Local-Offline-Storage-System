using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stock.Recognition;

namespace Stock.RecognitionLab;

internal static class RunExporter
{
    internal static string Export(string parent, RecognitionDiagnostic diagnostic, RunImage image, string source,
        EvaluationReport? comparison, string decision, string notes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(image.Path)));
        if (hash != diagnostic.ImageSha256) throw new RecognitionException("导出截图与本次请求的哈希不一致，请重新测试。");
        var directory = Path.Combine(Path.GetFullPath(parent), "run-" + DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(directory);
        var filename = "uploaded-image" + Path.GetExtension(image.Path);
        File.Copy(image.Path, Path.Combine(directory, filename));
        var manifest = new
        {
            module = ModuleBuildInfo.Current,
            source, realCallEvidence = source == "real-call" && diagnostic.HttpStatus.HasValue && diagnostic.RawResponse.Length > 0,
            usableRecognitionResult = diagnostic.Result is not null, sample = comparison?.Sample,
            image = new { file = filename, image.OriginalName, image.OriginalSha256, uploadedSha256 = hash, image.Width, image.Height, image.Steps },
            diagnostic, recognition = diagnostic.Result, comparison,
            review = new { decision, notes, recordedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)), integrationApproved = false }
        };
        Write("run.json", JsonSerializer.Serialize(manifest, RecognitionJson.Options));
        Write("response.txt", diagnostic.RawResponse); Write("model-output.txt", diagnostic.ModelText);
        if (diagnostic.Result is not null) Write("recognition.json", JsonSerializer.Serialize(diagnostic.Result, RecognitionJson.Options));
        if (comparison is not null) Write("comparison.json", JsonSerializer.Serialize(comparison, RecognitionJson.Options));
        Write("review.txt", decision + "\n" + notes);
        var report = new StringBuilder("# 千问图像识别测试记录\n\n");
        report.AppendLine(source == "real-call" ? "来源：真实百炼调用。" : "来源：本地响应回放，未发送网络请求；不能代替真实调用验收。");
        report.AppendLine($"\n模型：{diagnostic.Model}；提示词：{diagnostic.PromptVersion}；阶段：{diagnostic.Stage}。");
        report.AppendLine($"\n测试 EXE 版本：{ModuleBuildInfo.Current.Version}；SHA-256：{ModuleBuildInfo.Current.Sha256}。");
        report.AppendLine($"\n截图 SHA-256：{hash}\n\n耗时：{diagnostic.Elapsed.TotalSeconds:F2} 秒；输入 Token：{diagnostic.InputTokens?.ToString() ?? "未返回"}；输出 Token：{diagnostic.OutputTokens?.ToString() ?? "未返回"}。");
        if (diagnostic.Error is not null) report.AppendLine("\n错误：" + diagnostic.Error);
        if (comparison is not null)
        {
            report.AppendLine("\n" + comparison.Summary);
            report.AppendLine("\n| 行 | 字段 | 人工基准 | 模型原值 | 解析结果 | 差异来源 |\n| --- | --- | --- | --- | --- | --- |");
            foreach (var d in comparison.Differences) report.AppendLine($"| {d.Row} | {MainWindow.FieldLabel(d.Field)} | {Cell(d.Expected)} | {Cell(d.Model)} | {Cell(d.Parsed)} | {d.Category} |");
            report.AppendLine("\n待人工确认：" + string.Join("；", comparison.PendingHumanFields.Select(p => $"第{p.Row}行 {MainWindow.FieldLabel(p.Field)}")));
        }
        report.AppendLine("\n人工意见：" + decision + "\n\n" + notes);
        report.AppendLine("\n启用状态：本报告仅记录人工核对意见，不自动启用或更新库存程序。用户确认真实结果后，由开发者生成启用同一识图 EXE 的升级安装包。\n");
        Write("report.md", report.ToString());
        return directory;
        void Write(string name, string text) => File.WriteAllText(Path.Combine(directory, name), text, new UTF8Encoding(false));
    }
    private static string Cell(string value) => (value.Length == 0 ? "（空）" : value).Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>");
}

using System.Text.Json;

namespace Stock.Recognition;

public sealed record SampleBaseline(string Id, string Image, int RowCount,
    Dictionary<RecognitionProductType, long> SectionTotals, List<Dictionary<string, JsonElement>> Rows)
{
    public override string ToString() => $"{Id}（{RowCount}行）";
    public static IReadOnlyList<SampleBaseline> Load(string path) => Parse(File.ReadAllText(path));
    public static IReadOnlyList<SampleBaseline> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Deserialize<List<SampleBaseline>>(document.RootElement.GetProperty("samples"), RecognitionJson.Options)
            ?? throw new RecognitionException("人工基准格式无效。");
    }
}
public sealed record FieldDifference(int Row, string Field, string Expected, string Model, string Parsed, string Category);
public sealed record PendingField(int Row, string Field);
public sealed record ParsingChange(int Row, string Field, string Model, string Parsed);
public sealed record EvaluationReport(string Sample, int ExpectedRows, int ActualRows, double? FieldAccuracy,
    int CheckedFields, IReadOnlyList<PendingField> PendingHumanFields, IReadOnlyList<FieldDifference> Differences,
    IReadOnlyList<ParsingChange> ParsingChanges, IReadOnlyDictionary<RecognitionProductType, long> ExpectedTotals,
    IReadOnlyDictionary<RecognitionProductType, long?> ReturnedTotals,
    IReadOnlyDictionary<RecognitionProductType, long?> CalculatedTotals, bool ReturnedTotalsMatch,
    bool CalculatedTotalsMatch, bool ActualQuantityColumnConfirmed, bool CriticalFieldsAllCorrect,
    string Acceptance = "需对照真实截图完成人工核对，由用户确认；本报告不自动批准回接。")
{
    public string Summary => $"{Sample}：预期 {ExpectedRows} 行，识别 {ActualRows} 行；字段差异 {Differences.Count} 项，" +
        $"待人工确认 {PendingHumanFields.Count} 项。实发列：{(ActualQuantityColumnConfirmed ? "已确认" : "未确认")}；" +
        $"明细合计：{(CalculatedTotalsMatch ? "一致" : "不一致或未知")}；识别分区合计：{(ReturnedTotalsMatch ? "一致" : "不一致或未返回")}。";
}

public static class SampleEvaluator
{
    private static readonly HashSet<string> AccuracyFields = ["name", "spec", "color", "materialCode", "marker"];
    private static readonly HashSet<string> CriticalFields = ["originalOrder", "type", "rawQuantity", "rawUnit", "materialCode", "marker"];

    public static EvaluationReport Evaluate(SampleBaseline baseline, RecognitionResult result, string modelText)
    {
        using var parsed = JsonDocument.Parse(JsonSerializer.Serialize(result, RecognitionJson.Options));
        JsonDocument? model = null;
        try
        {
            var text = modelText.Trim();
            if (text.StartsWith("```", StringComparison.Ordinal) && text.EndsWith("```", StringComparison.Ordinal))
                text = text[(text.IndexOf('\n') + 1)..^3].Trim();
            model = JsonDocument.Parse(text);
        }
        catch (JsonException) { }
        using (model)
        {
            var parsedRows = parsed.RootElement.GetProperty("rows");
            var modelRows = model is not null && model.RootElement.ValueKind == JsonValueKind.Object &&
                model.RootElement.TryGetProperty("rows", out var mr) && mr.ValueKind == JsonValueKind.Array ? mr : default;
            var differences = new List<FieldDifference>();
            var changes = new List<ParsingChange>();
            var pending = new List<PendingField>();
            int checkedFields = 0, correct = 0;
            for (var index = 0; index < baseline.Rows.Count; index++)
            {
                var row = index < parsedRows.GetArrayLength() ? parsedRows[index] : default;
                var rawRow = modelRows.ValueKind == JsonValueKind.Array && index < modelRows.GetArrayLength() ? modelRows[index] : default;
                foreach (var (field, expectedElement) in baseline.Rows[index])
                {
                    var actual = Value(row, field);
                    var raw = Value(rawRow, field);
                    if (actual != raw) changes.Add(new(index + 1, field, raw, actual));
                    if (expectedElement.ValueKind == JsonValueKind.Null)
                    {
                        pending.Add(new(index + 1, field));
                        continue;
                    }
                    var expected = Scalar(expectedElement);
                    if (AccuracyFields.Contains(field)) { checkedFields++; if (actual == expected) correct++; }
                    if (actual != expected || raw != expected)
                        differences.Add(new(index + 1, field, expected, raw, actual,
                            actual == expected ? "解析修正，需对照原图" : raw == expected ? "解析转换" : raw == actual ? "模型识别" : "模型识别与解析转换"));
                }
            }
            var totals = CalculateTotals(result);
            var returnedMatch = baseline.SectionTotals.All(t => result.SectionTotals.TryGetValue(t.Key, out var n) && n == t.Value);
            var calculatedMatch = baseline.SectionTotals.All(t => totals.TryGetValue(t.Key, out var n) && n == t.Value);
            var criticalCorrect = result.Rows.Count == baseline.RowCount && result.ActualQuantityColumn &&
                !differences.Any(d => CriticalFields.Contains(d.Field) && d.Parsed != d.Expected);
            return new(baseline.Id, baseline.RowCount, result.Rows.Count,
                checkedFields == 0 ? null : (double)correct / checkedFields, checkedFields, pending, differences, changes,
                baseline.SectionTotals, result.SectionTotals, totals, returnedMatch, calculatedMatch,
                result.ActualQuantityColumn, criticalCorrect);
        }
    }

    public static IReadOnlyDictionary<RecognitionProductType, long?> CalculateTotals(RecognitionResult result)
    {
        var totals = Enum.GetValues<RecognitionProductType>().Where(t => t != RecognitionProductType.Unknown)
            .ToDictionary(t => t, _ => (long?)0);
        foreach (var row in result.Rows)
        {
            if (!totals.ContainsKey(row.Type))
            {
                foreach (var type in totals.Keys.ToList()) totals[type] = null;
                continue;
            }
            if (RecognitionParser.Quantity(row.RawQuantity, out var n) && totals[row.Type].HasValue) totals[row.Type] += n;
            else totals[row.Type] = null;
        }
        return totals;
    }

    private static string Value(JsonElement row, string field) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(field, out var value) ? Scalar(value) : "（未返回）";
    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Null => "（未知）",
        _ => value.GetRawText()
    };
}

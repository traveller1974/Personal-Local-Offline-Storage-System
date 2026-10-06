using System.Globalization;

namespace Stock.Core;

public static class IntegerInput
{
    public static bool TryParse(string? text, long minimum, out long value)
    {
        value = 0;
        return !string.IsNullOrEmpty(text) && text.All(c => c is >= '0' and <= '9')
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value >= minimum && value <= Rules.MaxQuantity;
    }
}

public sealed record OcrBlock(string Text, double Confidence, double[][] Box)
{
    public double X => Box.Average(p => p[0]);
    public double Y => Box.Average(p => p[1]);
    public double Height => Math.Max(1, Box.Max(p => p[1]) - Box.Min(p => p[1]));
}
public sealed record OcrResponse(string RequestId, bool Success, int ImageWidth, int ImageHeight,
    IReadOnlyList<OcrBlock>? Blocks, string? ErrorCode = null, string? Message = null);
public sealed record QuantityColumn(string Id, string Label, double X);
public sealed record InvoiceRow(string Name, string Spec, string RawQuantity, double Confidence, double[][] Box, string Warning);
public sealed record InvoiceParseResult(IReadOnlyList<QuantityColumn> QuantityColumns, IReadOnlyList<InvoiceRow> Rows, bool NeedsColumn, string Message);

/// <summary>Position based table extraction. Quantity is never inferred from price or amount.</summary>
public static class InvoiceParser
{
    private static readonly HashSet<string> Names = ["品名", "物料名称", "商品名称", "货品名称", "产品名称", "名称"];
    private static readonly HashSet<string> Specs = ["规格", "型号", "规格型号", "规格/型号", "规格（型号）"];
    private static readonly HashSet<string> Quantities = ["实发", "实发数量", "数量", "实收数量", "实收", "送货数量", "订货数量"];
    private static readonly HashSet<string> Others = ["序号", "编号", "颜色", "计划", "欠发", "标识", "单位", "单价", "金额", "含税单价", "备注", "合计金额"];
    private static string Header(string text) => Rules.Clean(text).Replace(" ", "").Replace("\n", "").Trim(':', '：');

    public static InvoiceParseResult Parse(OcrResponse response, string? quantityColumnId = null)
    {
        var blocks = (response.Blocks ?? []).Where(b => b.Box.Length == 4 && b.Box.All(p => p.Length == 2)
            && !string.IsNullOrWhiteSpace(b.Text)).OrderBy(b => b.Y).ToList();
        var name = blocks.FirstOrDefault(b => Names.Contains(Header(b.Text)));
        if (name is null) return new([], [], false, "没有找到品名表头，请手动录入，或裁剪至表格后重试。");
        var header = blocks.Where(b => Math.Abs(b.Y - name.Y) <= Math.Max(name.Height, b.Height) * 0.85
            && (Names.Contains(Header(b.Text)) || Specs.Contains(Header(b.Text)) || Quantities.Contains(Header(b.Text)) || Others.Contains(Header(b.Text))))
            .OrderBy(b => b.X).ToList();
        var quantities = header.Where(b => Quantities.Contains(Header(b.Text)))
            .Select((b, i) => new QuantityColumn(i.ToString(CultureInfo.InvariantCulture), b.Text + $"（第{i + 1}列）", b.X)).ToList();
        if (quantities.Count == 0) return new([], [], false, "没有找到数量表头；单价和金额不会用于入库数量。请手动录入或更换照片。");
        var selected = quantities.Count == 1 ? quantities[0] : quantities.FirstOrDefault(c => c.Id == quantityColumnId);
        if (selected is null) return new(quantities, [], true, "存在多个数量列，请明确选择本次采用的数量列。");
        var nameIndex = header.IndexOf(name);
        var specIndex = header.FindIndex(b => Specs.Contains(Header(b.Text)));
        var quantityIndex = header.FindIndex(b => b.X == selected.X && Quantities.Contains(Header(b.Text)));
        int Column(OcrBlock block)
        {
            for (var i = 0; i < header.Count - 1; i++) if (block.X < (header[i].X + header[i + 1].X) / 2) return i;
            return header.Count - 1;
        }
        var rowGroups = new List<List<OcrBlock>>();
        foreach (var block in blocks.Where(b => b.Y > name.Y + name.Height * 0.9))
        {
            var group = rowGroups.LastOrDefault();
            if (group is null || Math.Abs(group.Average(b => b.Y) - block.Y) > Math.Max(block.Height, group.Average(b => b.Height)) * 0.75)
                rowGroups.Add([block]);
            else group.Add(block);
        }
        var rows = new List<InvoiceRow>();
        foreach (var group in rowGroups)
        {
            string Cell(int index) => index < 0 ? "" : string.Join(" ", group.Where(b => Column(b) == index).OrderBy(b => b.X).Select(b => b.Text)).Trim();
            var n = Cell(nameIndex); var s = Cell(specIndex); var raw = Cell(quantityIndex);
            if (new[] { "合计", "总计", "小计", "金额", "单价", "大写", "收货人", "送货人", "签名" }.Any(t => Header(n).StartsWith(t, StringComparison.Ordinal))) break;
            if (Names.Contains(Header(n))) continue;
            if (string.IsNullOrWhiteSpace(n) && string.IsNullOrWhiteSpace(raw)) continue;
            var relevant = group.Where(b => Column(b) == nameIndex || Column(b) == specIndex || Column(b) == quantityIndex).ToList();
            var confidence = relevant.Count == 0 ? 0 : relevant.Min(b => b.Confidence);
            var warnings = new List<string>();
            if (string.IsNullOrWhiteSpace(n)) warnings.Add("缺少货品名称");
            if (!IntegerInput.TryParse(raw, 1, out _)) warnings.Add("数量缺失或不是有效整数");
            if (confidence < 0.90) warnings.Add("识别置信度低，请对照照片");
            var x0 = group.Min(b => b.Box.Min(p => p[0])); var x1 = group.Max(b => b.Box.Max(p => p[0]));
            var y0 = group.Min(b => b.Box.Min(p => p[1])); var y1 = group.Max(b => b.Box.Max(p => p[1]));
            rows.Add(new(n, s, raw, confidence, [[x0,y0],[x1,y0],[x1,y1],[x0,y1]], string.Join("；", warnings)));
        }
        return new(quantities, rows, false, rows.Count == 0 ? "没有提取到货品明细，请对照照片手动录入。" : "请逐行确认名称、规格和数量；勾选已核对后才可加入进货清单。");
    }
}

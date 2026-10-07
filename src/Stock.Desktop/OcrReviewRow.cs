using Stock.Core;
using System.Runtime.CompilerServices;

namespace Stock.Desktop;

public sealed record ReviewProblem(string Field, string Message);

public sealed class OcrReviewRow : Observable
{
    private string name, spec, quantity, color = "", code = "", marker = "", rawUnit = "";
    private ProductType type;
    private Product? product;
    private bool reviewed, unitConfirmed, matching, applyingAutomatic;
    private readonly HashSet<string> humanFields=[];
    private readonly HashSet<string> catalogFields=[];
    public string RowId { get; } = Guid.NewGuid().ToString("N");
    public bool HumanEdited { get; private set; }
    public bool AutomaticallyFilled { get; private set; }
    public string MatchSource { get; private set; } = "识别结果";
    public IReadOnlyDictionary<string,string> FieldSources => new[]{"Name","Spec","Color","MaterialCode","Type","Quantity","RawUnit","Product"}
        .ToDictionary(property=>property,property=>humanFields.Contains(property)?"人工填写":catalogFields.Contains(property)?MatchSource:"照片识别");
    public InvoiceRow Source { get; }
    public RecognizedRow? CloudSource { get; }
    public bool Cloud => CloudSource is not null;
    public string OriginalOrder => CloudSource?.OriginalOrder ?? "";
    public string RawName => CloudSource?.RawName ?? Source.Name;
    public string RawQuantity => CloudSource?.RawQuantity ?? Source.RawQuantity;
    public string QuantityColumnHelp => CloudSource is null ? "" : "所取数量列："+(CloudSource.QuantityColumn.Length==0?"未看清":CloudSource.QuantityColumn)+
        (CloudSource.QuantityCandidates.Count==0?"":"；其他数量列："+string.Join("；",CloudSource.QuantityCandidates.Select(c=>$"{c.Header} {c.Value}")));
    public string SourceRawUnit => CloudSource?.RawUnit ?? "";
    public string SourceSpec => CloudSource?.Spec ?? Source.Spec;
    public string SourceColor => CloudSource?.Color ?? "";
    public string SourceCode => CloudSource?.MaterialCode ?? "";
    public string SourceType => CloudSource is null ? "未识别" : Rules.TypeName(CloudSource.Type);
    public string SectionEvidence => CloudSource?.SectionEvidence ?? "";
    public string RecognitionIssues => CloudSource is null ? Source.Confidence < .90 ? "识别可能有误，请对照照片检查。" : "" : string.Join("；", CloudSource.Issues);
    public IReadOnlyList<ProductType> Types { get; } = [ProductType.Unknown, ProductType.Vehicle, ProductType.Battery, ProductType.Charger, ProductType.Accessory];
    public IReadOnlyList<Choice<ProductType>> TypeChoices { get; } = Enum.GetValues<ProductType>().Select(t => new Choice<ProductType>(t == ProductType.Unknown ? "请选择类型" : Rules.TypeName(t), t)).ToList();
    public ProductType Type { get => type; set { if (Set(ref type, value)) { Rematch(true); Changed(nameof(Unit)); } } }
    public string Color { get => color; set { if (Set(ref color, value)) Rematch(); } }
    public string MaterialCode { get => code; set { if (Set(ref code, value)) Rematch(); } }
    public string Marker { get => marker; set { if (Set(ref marker, value)) Invalidate(); } }
    public string RawUnit { get => rawUnit; set { if (Set(ref rawUnit, value)) Invalidate(true); } }
    public string Unit => Type == ProductType.Unknown ? Product?.Unit ?? "待确认" : Rules.Unit(Type);
    public IReadOnlyList<Product> Products { get; private set; }
    public string Name { get => name; set { if (Set(ref name, value)) Rematch(); } }
    public string Spec { get => spec; set { if (Set(ref spec, value)) Rematch(); } }
    public string Quantity { get => quantity; set { if (Set(ref quantity, value)) Invalidate(); } }
    public Product? Product { get => product; set { if (Set(ref product, value)) { Invalidate(true); Changed(nameof(Unit)); } } }
    public bool Matching { get => matching; internal set { if (Set(ref matching, value)) NotifyReview(); } }
    public bool Reviewed
    {
        get => reviewed;
        set
        {
            Set(ref reviewed, value && CanReview);
            Changed(nameof(ReviewStatus));
        }
    }
    public string ExpectedRawUnit => Type switch { ProductType.Vehicle or ProductType.Charger => "PC", ProductType.Battery => "PAA", ProductType.Accessory => "Z1", _ => "" };
    public bool UnitConfirmationRequired => Cloud && Type != ProductType.Unknown && !UnitMatches;
    private bool UnitMatches => Type != ProductType.Unknown && (RawUnit == ExpectedRawUnit || RawUnit == Unit);
    public bool UnitConfirmed { get => unitConfirmed; set { if (Set(ref unitConfirmed, value)) Invalidate(); } }
    public string UnitConfirmationText => $"我已对照照片确认：这里填写的数量按“{Unit}”计，不需要换算。";
    public string RawUnitHelp => !Cloud ? "请按所选货品的单位填写数量。" :
        Type == ProductType.Unknown ? "先选择货品类型，再确认照片上的单位。" :
        UnitConfirmationRequired ? $"照片单位为“{Empty(RawUnit)}”。本次进货数量按“{Unit}”填写，需要换算时直接改数量。这条提示不影响加入清单。" :
        $"照片单位已匹配，本次进货数量按“{Unit}”计。";
    public string UnitReviewNote => Cloud && (UnitConfirmationRequired && UnitConfirmed || SourceRawUnit != RawUnit) ?
        $"原单识别单位：{Empty(SourceRawUnit)}；核对单位：{Empty(RawUnit)}；人工确认数量按{Unit}计，不换算。" : "";
    public IReadOnlyList<ReviewProblem> ReviewProblems
    {
        get
        {
            var problems = new List<ReviewProblem>();
            if (Matching) problems.Add(new("Product", "正在查找已有货品，请稍候。"));
            if (Cloud && Type == ProductType.Unknown) problems.Add(new("Type", "请选择货品类型：成车、电池、充电器或附件。"));
            if (Product is null) problems.Add(new("Product", "请选择已有货品，或点击“新建货品”填写资料。"));
            else
            {
                if (!Product.Active) problems.Add(new("Product", "这个货品已停用，请选择启用的货品。"));
                if (Cloud && !Product.Complete) problems.Add(new("Product", "这个货品资料未填完整，请先在库存首页编辑并补全。"));
                if (Cloud && Product.Type != Type) problems.Add(new("Type", "货品类型与所选货品不同，请重新选择货品。"));
                foreach (var difference in IdentityDifferences(Product).Where(d => d.Field != "Type"))
                    problems.Add(new(difference.Field, $"{difference.Label}与所选货品不同，请修改，或重新选择货品并使用它的资料。"));
            }
            if (Cloud && (Type is ProductType.Battery or ProductType.Charger) && Color.Length > 0)
                problems.Add(new("Color", "电池和充电器不记录颜色，请清空颜色。"));
            if (!IntegerInput.TryParse(Quantity, 0, out _))
                problems.Add(new("Quantity", $"请填写本次进货数量，只能填0到{Rules.MaxQuantity}的整数。"));
            return problems;
        }
    }
    public bool CanReview => ReviewProblems.Count == 0;
    public string Warning => string.Join(Environment.NewLine, ReviewProblems.Select(p => "• " + p.Message)
        .Concat(UnitConfirmationRequired && !UnitConfirmed ? ["• 请按所选货品的单位检查数量，必要时直接修改。"] : []));
    public string ReviewStatus => !CanReview ? "请补齐下方提示的内容。" : Reviewed ? "已核对。加入清单时使用当前填写的值。" :
        AutomaticallyFilled && !HumanEdited ? $"已自动填写（{MatchSource}）。可直接加入清单，也可修改。" : "可以加入清单；“已核对”可用来标记检查过的行。";

    public OcrReviewRow(InvoiceRow source, IReadOnlyList<Product> products)
    { Source = source; Products = products; name = source.Name; spec = source.Spec; quantity = source.RawQuantity; applyingAutomatic=true;Rematch();applyingAutomatic=false; }
    public OcrReviewRow(RecognizedRow source, IReadOnlyList<Product> products)
    {
        CloudSource = source; Source = new(source.Name, source.Spec, source.RawQuantity, 0, [], "");
        Products = products.Where(p => p.Complete).ToList(); name = source.Name; spec = source.Spec; quantity = source.RawQuantity;
        type = source.Type; color = source.Color; code = source.MaterialCode; marker = source.Marker; rawUnit = source.RawUnit; applyingAutomatic=true;Rematch();applyingAutomatic=false;
    }
    public IReadOnlyList<(string Field, string Label, string Current, string Selected)> IdentityDifferences(Product p)
    {
        var result = new List<(string, string, string, string)>();
        bool Different(string a, string b) => Cloud ? Rules.ExactIdentity(a) != Rules.ExactIdentity(b) : Rules.Identity(a) != Rules.Identity(b);
        if (Cloud && p.Type != Type) result.Add(("Type", "货品类型", Type == ProductType.Unknown ? "未选择" : Rules.TypeName(Type), p.TypeText));
        if (Different(Name, p.Name)) result.Add(("Name", "货品名称", Name, p.Name));
        if (Different(Spec, p.Spec)) result.Add(("Spec", "规格", Spec, p.Spec));
        if (Cloud && Different(Color, p.Color ?? "")) result.Add(("Color", "颜色", Color, p.Color ?? ""));
        if (Cloud && Different(MaterialCode, p.MaterialCode ?? "")) result.Add(("MaterialCode", "货品编码", MaterialCode, p.MaterialCode ?? ""));
        return result;
    }
    private void Invalidate(bool clearUnit = false,[CallerMemberName]string field="")
    {
        if (!applyingAutomatic) { HumanEdited = true; AutomaticallyFilled = false; humanFields.Add(field); }
        if (clearUnit) Set(ref unitConfirmed, false, nameof(UnitConfirmed));
        NotifyReview();
    }
    private void NotifyReview()
    {
        foreach (var property in new[] { nameof(CanReview), nameof(ReviewProblems), nameof(Warning), nameof(ReviewStatus), nameof(UnitConfirmationRequired), nameof(UnitConfirmationText), nameof(RawUnitHelp), nameof(UnitReviewNote) }) Changed(property);
    }
    private bool IdentityMatches(Product p) => IdentityDifferences(p).Count == 0;
    private void Rematch(bool clearUnit = false,[CallerMemberName]string field="") { var matches=Products.Where(IdentityMatches).ToArray(); Product = matches.Length==1?matches[0]:null; Invalidate(clearUnit,field); }
    public void RefreshProducts(IReadOnlyList<Product> products, Product? chosen = null)
    {
        Products = Cloud ? products.Where(p => p.Complete).ToList() : products;
        Changed(nameof(Products));
        var matches=Products.Where(IdentityMatches).ToArray(); Product = chosen ?? (matches.Length==1?matches[0]:null);
        NotifyReview();
    }
    internal void ApplyAutomaticProduct(Product selected, string source, bool quantityReliable)
    {
        applyingAutomatic=true;
        try
        {
            if(name!=selected.Name)catalogFields.Add("Name");if(spec!=selected.Spec)catalogFields.Add("Spec");if(color!=(selected.Color??""))catalogFields.Add("Color");
            if(code!=(selected.MaterialCode??""))catalogFields.Add("MaterialCode");if(type!=selected.Type)catalogFields.Add("Type");catalogFields.Add("Product");
            type=selected.Type; name=selected.Name; spec=selected.Spec; color=selected.Color??""; code=selected.MaterialCode??"";
            Product=selected; MatchSource=source; AutomaticallyFilled=quantityReliable && CanReview && UnitMatches;
            HumanEdited=false;
            foreach(var property in new[]{nameof(Type),nameof(Name),nameof(Spec),nameof(Color),nameof(MaterialCode),nameof(Unit)})Changed(property);
            NotifyReview();
        }
        finally { applyingAutomatic=false; }
    }
    private static string Empty(string text) => text.Length == 0 ? "未填写" : text;
}

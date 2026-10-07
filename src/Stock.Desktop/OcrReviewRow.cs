using Stock.Core;

namespace Stock.Desktop;

public sealed record ReviewProblem(string Field, string Message);

public sealed class OcrReviewRow : Observable
{
    private string name, spec, quantity, color = "", code = "", marker = "", rawUnit = "";
    private ProductType type;
    private Product? product;
    private bool reviewed, unitConfirmed, reviewChanged, matching;
    public InvoiceRow Source { get; }
    public RecognizedRow? CloudSource { get; }
    public bool Cloud => CloudSource is not null;
    public string OriginalOrder => CloudSource?.OriginalOrder ?? "";
    public string RawName => CloudSource?.RawName ?? Source.Name;
    public string RawQuantity => CloudSource?.RawQuantity ?? Source.RawQuantity;
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
            if (reviewed) reviewChanged = false;
            Changed(nameof(ReviewStatus));
        }
    }
    public string ExpectedRawUnit => Type switch { ProductType.Vehicle or ProductType.Charger => "PC", ProductType.Battery => "PAA", ProductType.Accessory => "Z1", _ => "" };
    public bool UnitConfirmationRequired => Cloud && Type != ProductType.Unknown && RawUnit != ExpectedRawUnit;
    public bool UnitConfirmed { get => unitConfirmed; set { if (Set(ref unitConfirmed, value)) Invalidate(); } }
    public string UnitConfirmationText => $"我已对照照片确认：这里填写的数量按“{Unit}”计，不需要换算。";
    public string RawUnitHelp => !Cloud ? "离线识别时，请对照照片确认数量使用的单位。" :
        Type == ProductType.Unknown ? "先选择货品类型，再确认照片上的单位。" :
        UnitConfirmationRequired ? $"照片单位识别为“{Empty(RawUnit)}”，所选货品按“{Unit}”入库。请对照照片确认下方说明；需要换算时，先手动改好本次进货数量。" :
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
            if (UnitConfirmationRequired && !UnitConfirmed)
                problems.Add(new("Unit", $"照片单位与“{Unit}”的常用代码不同或为空，请对照照片，确认下方单位说明；需要换算时先手动改好数量。"));
            return problems;
        }
    }
    public bool CanReview => ReviewProblems.Count == 0;
    public string Warning => string.Join(Environment.NewLine, ReviewProblems.Select(p => "• " + p.Message));
    public string ReviewStatus => Reviewed ? "已核对，可以加入进货清单。" : reviewChanged ? "内容已修改，请重新核对。" : CanReview ? "请对照照片检查货品和数量，再勾选“已核对”。" : "请先处理下方提示，再勾选“已核对”。";

    public OcrReviewRow(InvoiceRow source, IReadOnlyList<Product> products)
    { Source = source; Products = products; name = source.Name; spec = source.Spec; quantity = source.RawQuantity; Rematch(); }
    public OcrReviewRow(RecognizedRow source, IReadOnlyList<Product> products)
    {
        CloudSource = source; Source = new(source.Name, source.Spec, source.RawQuantity, 0, [], "");
        Products = products.Where(p => p.Complete).ToList(); name = source.Name; spec = source.Spec; quantity = source.RawQuantity;
        type = source.Type; color = source.Color; code = source.MaterialCode; marker = source.Marker; rawUnit = source.RawUnit; Rematch();
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
    private void Invalidate(bool clearUnit = false)
    {
        if (reviewed) reviewChanged = true;
        Set(ref reviewed, false, nameof(Reviewed));
        if (clearUnit) Set(ref unitConfirmed, false, nameof(UnitConfirmed));
        NotifyReview();
    }
    private void NotifyReview()
    {
        foreach (var property in new[] { nameof(CanReview), nameof(ReviewProblems), nameof(Warning), nameof(ReviewStatus), nameof(UnitConfirmationRequired), nameof(UnitConfirmationText), nameof(RawUnitHelp), nameof(UnitReviewNote) }) Changed(property);
    }
    private bool IdentityMatches(Product p) => IdentityDifferences(p).Count == 0;
    private void Rematch(bool clearUnit = false) { Product = Products.FirstOrDefault(IdentityMatches); Invalidate(clearUnit); }
    public void RefreshProducts(IReadOnlyList<Product> products, Product? chosen = null)
    {
        Products = Cloud ? products.Where(p => p.Complete).ToList() : products;
        Changed(nameof(Products));
        Product = chosen ?? Products.FirstOrDefault(IdentityMatches);
        NotifyReview();
    }
    private static string Empty(string text) => text.Length == 0 ? "未填写" : text;
}

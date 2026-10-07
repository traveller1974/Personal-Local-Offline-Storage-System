using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Stock.Core;

namespace Stock.Desktop;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field,value)) return false; field=value; Changed(name); return true; }
}
public sealed record Choice<T>(string Label, T Value) { public override string ToString() => Label; }
public sealed record RecordRow(DocumentRecord Document)
{
    public string Id => Document.Id;
    public string Number => Document.Number;
    public string Time => DateTimeOffset.Parse(Document.OccurredAt).ToString("yyyy-MM-dd HH:mm:ss zzz");
    public string Date => Rules.DateText(Document.BusinessDate);
    public string Kind => Rules.KindName(Document.Kind);
    public string Channel => Document.Channel;
    public string Status => Document.Status == RecordStatus.Valid ? "有效" : "已作废";
    public string Items => string.Join("；",Document.Lines.Select(l => $"{l.Name} {l.Spec} × {l.Quantity}{l.Unit}"));
}
public sealed class MainViewModel : Observable
{
    public StockService Service { get; }
    public ObservableCollection<Product> Products { get; } = [];
    public ObservableCollection<RecordRow> Records { get; } = [];
    public IReadOnlyList<Choice<DatePreset>> Presets { get; } = [new("今日",DatePreset.Today),new("昨日",DatePreset.Yesterday),new("过去7天",DatePreset.Last7Days),new("上个月",DatePreset.PreviousMonth),new("过去3个月",DatePreset.Last3Months),new("过去6个月",DatePreset.Last6Months),new("去年",DatePreset.PreviousYear),new("自选日期",DatePreset.Custom)];
    public IReadOnlyList<Choice<DocumentKind?>> Kinds { get; } = [new("全部",null),new("进货",DocumentKind.Purchase),new("出货",DocumentKind.Sale)];
    public IReadOnlyList<Choice<StatusFilter>> Statuses { get; } = [new("有效",StatusFilter.Valid),new("已作废",StatusFilter.Voided),new("全部",StatusFilter.All)];
    private string search=""; public string Search { get => search; set { if(Set(ref search,value)) RefreshProducts(); } }
    private bool inactive; public bool IncludeInactive { get=>inactive; set { if(Set(ref inactive,value)) RefreshProducts(); } }
    private Choice<DatePreset>? preset; public Choice<DatePreset>? Preset { get=>preset; set { if(Set(ref preset,value) && value is not null && value.Value!=DatePreset.Custom) { var dates=Rules.Dates(value.Value,DateOnly.FromDateTime(DateTime.Now)); Start=dates.Start.ToDateTime(TimeOnly.MinValue); End=dates.End.ToDateTime(TimeOnly.MinValue); } } }
    private DateTime? start=DateTime.Today;public DateTime? Start { get=>start;set=>Set(ref start,value); }
    private DateTime? end=DateTime.Today;public DateTime? End { get=>end;set=>Set(ref end,value); }
    public Choice<DocumentKind?>? Kind { get; set; }
    public string Channel { get; set; } = "全部";
    public Choice<StatusFilter>? Status { get; set; }
    public string RecordSearch { get; set; } = "";
    public bool Inventory { get; set; }
    public QueryFilter? AppliedFilter { get; private set; }
    public ProductFilter? ProductSelection { get; set; }
    public ProductFilter? RecordSelection { get; set; }
    private int page,productPage; private long recordCount,productCount;
    public int ProductPage { get=>productPage;set{productPage=(int)Math.Clamp(value,0,Math.Max(0,(productCount-1)/100));RefreshProducts(false);} }
    public string ProductPageText=>$"第{productPage+1}页 · 共{productCount}种货品";
    public int Page { get=>page; set { page=(int)Math.Clamp(value,0,Math.Max(0,(recordCount-1)/100)); ShowPage(); } }
    public string PageText => $"第{page+1}页 / {Math.Max(1,(recordCount+99)/100)}页 · 共{recordCount}张单据";
    public string RetentionText => "仅保留最近3年记录，保留起始日期：" + Rules.DateText(Service.Cutoff);
    private string notice="库存本机保存 · 云端识别仅上传确认截图"; public string Notice { get=>notice; set=>Set(ref notice,value); }
    public MainViewModel(StockService service) { Service=service; preset=Presets[0]; Kind=Kinds[0]; Status=Statuses[0]; Refresh(); }
    public void RefreshProducts() => RefreshProducts(true);
    private void RefreshProducts(bool reset) { if(reset)productPage=0;var batch=Service.ProductPage(ProductSelection,Search,IncludeInactive,productPage);productCount=batch.Count;Products.Clear();foreach(var p in batch.Items)Products.Add(p);Changed(nameof(ProductPageText)); }
    public void Refresh() { RefreshProducts(); if(AppliedFilter is not null) LoadRecords(AppliedFilter); Changed(nameof(RetentionText)); }
    public QueryFilter Filter()
    {
        if(Start is null || End is null) throw new BusinessException("请选择有效的开始和结束日期。");
        return new(DateOnly.FromDateTime(Start.Value),DateOnly.FromDateTime(End.Value),Kind?.Value,Channel=="全部"?null:Channel,RecordSearch,Status?.Value??StatusFilter.Valid,Inventory,RecordSelection);
    }
    public void Query() => LoadRecords(Filter());
    private void LoadRecords(QueryFilter filter) { AppliedFilter=filter; page=0; ShowPage(); Notice=filter.Start<Service.Cutoff?"查询包含已过保留期的日期；该部分记录已删除。":"查询已更新"; }
    private void ShowPage() { if(AppliedFilter is null)return;var batch=Service.QueryPage(AppliedFilter,page);recordCount=batch.Count;Records.Clear();foreach(var d in batch.Items)Records.Add(new(d));Changed(nameof(PageText)); }
    public async Task ExportAsync(string path) { Query();var filter=AppliedFilter!;await Task.Run(()=>ExcelExporter.Export(Service,filter,path)); Notice="Excel 已导出完整筛选结果"; }
    public string Receipt(string id) { Refresh(); return Service.GetDocument(id).Number; }
    public async Task BackupAsync(string path) => await Task.Run(()=>Service.Backup(path));
    public async Task RestoreAsync(string path) { await Task.Run(()=>Service.Restore(path)); Refresh(); }
}
public sealed class DraftLine : Observable
{
    public RecognizedRow? RecognitionEvidence { get; init; }
    public string SourceRowId { get; init; } = Guid.NewGuid().ToString("N");
    public bool HumanEdited { get; init; }
    public IReadOnlyDictionary<string,string>? FieldSources { get; init; }
    public string InvoiceStyle { get; init; } = "";
    private bool quantityEdited;
    public int PhotoOrder { get; init; }
    public string OriginalOrder { get; init; }="";
    public string Marker { get; init; }="";
    public string RawUnit { get; init; }="";
    public string RawName { get; init; }="";
    public string ReviewNote { get; init; }="";
    public string PhotoTotalCorrection { get; init; }="";
    private bool reviewed=true;
    public bool Reviewed { get=>reviewed;set=>Set(ref reviewed,value&&IntegerInput.TryParse(Quantity,0,out _)); }
    public Product Product { get; }
    private string quantity="1";
    public string Quantity { get=>quantity; set { if(Set(ref quantity,value))quantityEdited=true; } }
    public bool HumanConfirmed => Reviewed || HumanEdited && new[]{"Name","Spec","Color","MaterialCode","Type","Product"}
        .Any(property=>FieldSources?.GetValueOrDefault(property)=="人工填写");
    public IReadOnlyDictionary<string,string>? FinalFieldSources => quantityEdited
        ?new Dictionary<string,string>(FieldSources??new Dictionary<string,string>()){["Quantity"]="人工填写"}:FieldSources;
    public DraftLine(Product product,string quantity) { Product=product; this.quantity=quantity; }
}
public sealed class DraftViewModel : Observable
{
    public StockService Service { get; }
    public DocumentKind Kind { get; }
    public long QuantityMinimum => Kind==DocumentKind.Purchase?0:1;
    public string Channel { get; set; }
    public ObservableCollection<DraftLine> Lines { get; }=[];
    public List<string> Photos { get; }=[];
    public Dictionary<string,string> PhotoMetadata { get; }=[];
    public HashSet<string> PhotoHashes { get; }=[];
    public Dictionary<int,IReadOnlyDictionary<ProductType,long?>> PhotoTotals { get; }=[];
    public string TotalCorrection { get; set; }="";
    public string SubmissionKey { get; }=Guid.NewGuid().ToString("N");
    public DraftViewModel(StockService service,DocumentKind kind) { Service=service; Kind=kind; Channel=kind==DocumentKind.Purchase?"厂家":""; }
    public void Add(Product product,long quantity)
    {
        Rules.Quantity(quantity,Kind==DocumentKind.Purchase); Lines.Add(new(product,quantity.ToString()));
    }
    public IReadOnlyList<LineInput> Inputs()
    {
        var inputs=Lines.Select(l=>IntegerInput.TryParse(l.Quantity,Kind==DocumentKind.Purchase?0:1,out var q)?new LineInput(l.Product.Id,q,l.PhotoOrder,l.OriginalOrder,l.Marker,l.RawUnit,l.RawName,string.Join("；",new[]{l.ReviewNote,TotalCorrection}.Where(n=>n.Length>0))):throw new BusinessException($"{l.Product.Display} 的数量必须是有效整数。")).ToList();
        return inputs;
    }
    public IReadOnlyList<StockImpact> Preview() { if(Kind==DocumentKind.Sale&&Channel is not("零售" or "批发"))throw new BusinessException("请先选择零售或批发。");return Service.Preview(Kind,Inputs()); }
    public Task<string> CommitAsync()
    {
        var lines=Inputs();
        var metadata=new Dictionary<string,string>(PhotoMetadata);
        foreach(var entry in PhotoMetadata)
        {
            var node=System.Text.Json.Nodes.JsonNode.Parse(entry.Value)?.AsObject();
            if(node is null||node["photoOrder"] is null)continue;
            var photoOrder=node["photoOrder"]!.GetValue<int>();
            var feedback=Lines.Select((line,index)=>(line,index)).Where(x=>x.line.PhotoOrder==photoOrder&&x.line.RecognitionEvidence is not null)
                .Select(x=>new RecognitionFeedbackRow(x.line.SourceRowId,x.index+1,x.line.RecognitionEvidence!,x.line.HumanConfirmed,x.line.FinalFieldSources)).ToArray();
            node["feedback"]=System.Text.Json.JsonSerializer.SerializeToNode(new RecognitionFeedback(Lines.FirstOrDefault(l=>l.PhotoOrder==photoOrder)?.InvoiceStyle??"",feedback));
            node["finalForm"]=System.Text.Json.JsonSerializer.SerializeToNode(lines.Where(l=>l.PhotoOrder==photoOrder));
            metadata[entry.Key]=node.ToJsonString();
        }
        return Task.Run(()=>Service.Commit(Kind,Channel,lines,SubmissionKey,Photos,metadata));
    }
}

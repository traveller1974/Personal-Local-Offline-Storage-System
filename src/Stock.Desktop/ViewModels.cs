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
    private int page; private IReadOnlyList<DocumentRecord> results=[];
    public int Page { get=>page; set { page=Math.Clamp(value,0,Math.Max(0,(results.Count-1)/100)); ShowPage(); } }
    public string PageText => $"第{page+1}页 / {Math.Max(1,(results.Count+99)/100)}页 · 共{results.Count}张单据";
    public string RetentionText => "仅保留最近5年记录，保留起始日期：" + Rules.DateText(Service.Cutoff);
    private string notice="本机保存 · 完全离线"; public string Notice { get=>notice; set=>Set(ref notice,value); }
    public MainViewModel(StockService service) { Service=service; preset=Presets[0]; Kind=Kinds[0]; Status=Statuses[0]; Refresh(); }
    public void RefreshProducts() { Products.Clear(); foreach(var p in Service.Products(Search,IncludeInactive)) Products.Add(p); }
    public void Refresh() { RefreshProducts(); if(AppliedFilter is not null) LoadRecords(AppliedFilter); Changed(nameof(RetentionText)); }
    public QueryFilter Filter()
    {
        if(Start is null || End is null) throw new BusinessException("请选择有效的开始和结束日期。");
        return new(DateOnly.FromDateTime(Start.Value),DateOnly.FromDateTime(End.Value),Kind?.Value,Channel=="全部"?null:Channel,RecordSearch,Status?.Value??StatusFilter.Valid,Inventory);
    }
    public void Query() => LoadRecords(Filter());
    private void LoadRecords(QueryFilter filter) { results=Service.Query(filter); AppliedFilter=filter; page=0; ShowPage(); Notice=filter.Start<Service.Cutoff?"查询包含已过保留期的日期；该部分记录已删除。":"查询已更新"; }
    private void ShowPage() { Records.Clear(); foreach(var d in results.Skip(page*100).Take(100))Records.Add(new(d)); Changed(nameof(PageText)); }
    public async Task ExportAsync(string path) { Query();var filter=AppliedFilter!;await Task.Run(()=>ExcelExporter.Export(Service,filter,path)); Notice="Excel 已导出完整筛选结果"; }
    public string Receipt(string id) { Refresh(); return Service.GetDocument(id).Number; }
    public async Task BackupAsync(string path) => await Task.Run(()=>Service.Backup(path));
    public async Task RestoreAsync(string path) { await Task.Run(()=>Service.Restore(path)); Refresh(); }
}
public sealed class DraftLine : Observable
{
    public Product Product { get; }
    private string quantity="1";
    public string Quantity { get=>quantity; set=>Set(ref quantity,value); }
    public DraftLine(Product product,string quantity) { Product=product; this.quantity=quantity; }
}
public sealed class DraftViewModel : Observable
{
    public StockService Service { get; }
    public DocumentKind Kind { get; }
    public string Channel { get; set; }
    public ObservableCollection<DraftLine> Lines { get; }=[];
    public List<string> Photos { get; }=[];
    public string SubmissionKey { get; }=Guid.NewGuid().ToString("N");
    public DraftViewModel(StockService service,DocumentKind kind) { Service=service; Kind=kind; Channel=kind==DocumentKind.Purchase?"厂家":""; }
    public void Add(Product product,long quantity)
    {
        Rules.Quantity(quantity); var existing=Lines.FirstOrDefault(l=>l.Product.Id==product.Id);
        if(existing is null)Lines.Add(new(product,quantity.ToString()));
        else { if(!IntegerInput.TryParse(existing.Quantity,1,out var current))throw new BusinessException("请先修正该货品在清单中的数量。"); var total=checked(current+quantity); Rules.Quantity(total); existing.Quantity=total.ToString(); }
    }
    public IReadOnlyList<LineInput> Inputs() => Lines.Select(l => IntegerInput.TryParse(l.Quantity,1,out var q)?new LineInput(l.Product.Id,q):throw new BusinessException($"{l.Product.Display} 的数量必须是1到2147483647之间的整数。")).ToList();
    public IReadOnlyList<StockImpact> Preview() { if(Kind==DocumentKind.Sale&&Channel is not("零售" or "批发"))throw new BusinessException("请先选择零售或批发。");return Service.Preview(Kind,Inputs()); }
    public Task<string> CommitAsync() { var lines=Inputs(); return Task.Run(()=>Service.Commit(Kind,Channel,lines,SubmissionKey,Photos)); }
}

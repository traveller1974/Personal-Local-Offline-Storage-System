using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Stock.Core;
using Xunit;

namespace Stock.Tests;

public sealed class TestClock : IClock
{
    public DateOnly Today { get; set; }=new(2026,10,5);
    public DateTimeOffset Now => new(Today.ToDateTime(new TimeOnly(12,0)),TimeSpan.FromHours(8));
}
public sealed class BusinessTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"stock-test-"+Guid.NewGuid().ToString("N"));
    private readonly TestClock clock=new();
    private StockService service;
    public BusinessTests() { service=new(Path.Combine(root,"Data"),clock); }
    private static string Key()=>Guid.NewGuid().ToString("N");
    private long New(long w=10,long s=3,string name="螺丝",string spec="M8")=>service.CreateProduct(name,spec,"件",w,s,Key());
    private string Buy(long id,long q,string? key=null)=>service.Commit(DocumentKind.Purchase,"厂家",[new(id,q)],key??Key());
    private string Sell(long id,long q)=>service.Commit(DocumentKind.Sale,"零售",[new(id,q)],Key());
    private Product P(long id)=>service.Products(includeInactive:true).Single(p=>p.Id==id);
    [Fact] public void A01To05_OpeningPurchaseSaleAndTransfer()
    {
        var id=New(); Assert.Equal(13,P(id).Total); Assert.Empty(service.Query(new(clock.Today,clock.Today)));
        Buy(id,5); Assert.Equal((15L,3L,18L),(P(id).Warehouse,P(id).Store,P(id).Total));
        Sell(id,4); Assert.Equal((11L,3L,14L),(P(id).Warehouse,P(id).Store,P(id).Total));
        service.Transfer(id,2,true,Key()); Assert.Equal((9L,5L,14L),(P(id).Warehouse,P(id).Store,P(id).Total));
        Assert.Throws<BusinessException>(()=>Sell(id,10)); Assert.Equal(14,P(id).Total); service.ValidateIntegrity();
    }
    [Fact] public void A06To08_VoidBalancesAndLedger()
    {
        var id=New(); var buy=Buy(id,5); service.Void(buy,"录错",Key()); Assert.Equal(10,P(id).Warehouse);
        var sale=Sell(id,4); service.Void(sale,"录错",Key()); Assert.Equal(10,P(id).Warehouse);
        Assert.Throws<BusinessException>(()=>service.Void(sale,"再次",Key())); service.ValidateIntegrity();
        Assert.Equal(RecordStatus.Voided,service.GetDocument(buy).Status);
    }
    [Fact] public void A07_WholeVoidRollsBackOnOneInsufficientLine()
    {
        var a=New(0,0); var b=New(0,0,"螺帽");
        var buy=service.Commit(DocumentKind.Purchase,"厂家",[new(a,5),new(b,5)],Key()); Sell(b,5);
        Assert.Throws<BusinessException>(()=>service.Void(buy,"错误",Key())); Assert.Equal(5,P(a).Warehouse); Assert.Equal(RecordStatus.Valid,service.GetDocument(buy).Status); service.ValidateIntegrity();
    }
    [Theory] [InlineData(-1)] [InlineData(2147483648)] public void A09_InvalidIntegerQuantity(long q)
    { var id=New(); Assert.Throws<BusinessException>(()=>Buy(id,q)); Assert.Equal(13,P(id).Total); }
    [Fact] public void A09_OverflowAndZeroOpening()
    { var id=New(0,0); Assert.Equal(0,P(id).Total); Buy(id,int.MaxValue); Assert.Throws<BusinessException>(()=>Buy(id,1)); Assert.Throws<BusinessException>(()=>New(int.MaxValue,1,"溢出")); }
    [Fact] public void A10_DuplicateLinesCannotBypassStock()
    { var id=New(10,0); Assert.Throws<BusinessException>(()=>service.Commit(DocumentKind.Sale,"批发",[new(id,6),new(id,6)],Key())); var d=service.Commit(DocumentKind.Purchase,"厂家",[new(id,3),new(id,4)],Key()); Assert.Equal(2,service.GetDocument(d).Lines.Count); Assert.Equal(17,P(id).Warehouse); }
    [Fact] public void A11_PreviewAndDuplicateSubmit()
    { var id=New(); service.Preview(DocumentKind.Purchase,[new(id,5)]); Assert.Equal(10,P(id).Warehouse); var key=Key(); var doc=Buy(id,5,key); Assert.Equal(doc,Buy(id,5,key)); Assert.Equal(15,P(id).Warehouse); }
    [Fact] public void A12_FailureRollsBackAllWrites()
    {
        var id=New(); service.FaultInjector=_=>throw new IOException("test fault");
        Assert.Throws<IOException>(()=>Buy(id,5)); service.FaultInjector=null;
        Assert.Equal(10,P(id).Warehouse); Assert.Empty(service.Query(new(clock.Today,clock.Today))); service.ValidateIntegrity();
    }
    [Fact] public void A16_DatesOn20261005()
    {
        Assert.Equal((new DateOnly(2026,9,29),clock.Today),Rules.Dates(DatePreset.Last7Days,clock.Today));
        Assert.Equal((new DateOnly(2026,9,1),new DateOnly(2026,9,30)),Rules.Dates(DatePreset.PreviousMonth,clock.Today));
        Assert.Equal((new DateOnly(2026,7,5),clock.Today),Rules.Dates(DatePreset.Last3Months,clock.Today));
        Assert.Equal((new DateOnly(2026,4,5),clock.Today),Rules.Dates(DatePreset.Last6Months,clock.Today));
        Assert.Equal((new DateOnly(2025,1,1),new DateOnly(2025,12,31)),Rules.Dates(DatePreset.PreviousYear,clock.Today));
        Assert.Throws<BusinessException>(()=>service.Query(new(clock.Today,clock.Today.AddDays(-1))));
    }
    [Fact] public void A16_LeapYearAndYearBoundary()
    {
        Assert.Equal(new DateOnly(2024,2,29),Rules.Dates(DatePreset.Last3Months,new DateOnly(2024,5,31)).Start);
        Assert.Equal((new DateOnly(2025,12,1),new DateOnly(2025,12,31)),Rules.Dates(DatePreset.PreviousMonth,new DateOnly(2026,1,1)));
        clock.Today=new(2024,2,29); Assert.Equal(new DateOnly(2021,2,28),service.Maintain().Cutoff);
    }
    [Fact] public void A17To18_ExcelExportsAllAndExcludesVoided()
    {
        var id=New(0,0,"=SUM(A1:A2)"); var first=Buy(id,1); for(var i=0;i<104;i++)Buy(id,1); service.Void(first,"录错",Key());
        var filter=new QueryFilter(clock.Today,clock.Today,Status:StatusFilter.All); var file=Path.Combine(root,"out.xlsx"); ExcelExporter.Export(service,filter,file);
        using var book=new XLWorkbook(file); Assert.Equal(106,book.Worksheet("明细").LastRowUsed()!.RowNumber()); Assert.Equal(104,book.Worksheet("汇总").Cell(2,6).GetValue<long>());
        Assert.False(book.Worksheet("明细").Cell(2,5).HasFormula); Assert.Equal(XLDataType.Number,book.Worksheet("明细").Cell(2,8).DataType);
        Assert.Equal(104,service.Query(new(clock.Today,clock.Today)).Count);
    }
    [Fact] public void A19_CarryForwardPreservesBalanceAndDeletesOnlyExpired()
    {
        clock.Today=new(2023,10,4); var id=New(10,3); var photo=Path.Combine(root,"source.jpg"); File.WriteAllBytes(photo,[1,2,3]);
        service.Commit(DocumentKind.Purchase,"厂家",[new(id,5)],Key(),[photo]);
        clock.Today=new(2023,10,5); Sell(id,2); clock.Today=new(2026,10,5);
        var result=service.Maintain(); Assert.Equal(new DateOnly(2023,10,5),result.Cutoff); Assert.Equal(2,result.DeletedDocuments); Assert.Equal(1,result.DeletedPhotos);
        Assert.Equal((13L,3L),(P(id).Warehouse,P(id).Store)); Assert.True(File.Exists(photo)); Assert.Single(service.Query(new(new(2023,10,5),clock.Today)));
        Assert.Equal(0,service.Maintain().DeletedDocuments); service.ValidateIntegrity();
    }
    [Fact] public void A20_OldOriginalAndRetainedReversal()
    {
        clock.Today=new(2021,10,4); var id=New(0,0); var buy=Buy(id,5);
        clock.Today=new(2026,10,5); var reversal=service.Void(buy,"过期原单测试",Key()); service.Maintain();
        Assert.Equal(0,P(id).Total); Assert.Equal(buy,service.GetDocument(reversal).OriginalId); Assert.Throws<BusinessException>(()=>service.GetDocument(buy)); service.ValidateIntegrity();
    }
    [Fact] public void A19_MaintenanceFailureLeavesPhotosAndDatabase()
    {
        clock.Today=new(2020,1,1); var id=New(); clock.Today=new(2026,10,5); service.FaultInjector=_=>throw new IOException("fail");
        Assert.Throws<IOException>(()=>service.Maintain()); service.FaultInjector=null; Assert.Equal(13,P(id).Total); Assert.Single(service.Query(new(new(2020,1,1),new(2020,1,1),Inventory:true))); service.ValidateIntegrity();
    }
    [Fact] public void A21_BackupRestoreAndDamagedArchive()
    {
        var id=New(); var zip=Path.Combine(root,"backup.stockbackup"); service.Backup(zip); Buy(id,5); service.Restore(zip); Assert.Equal(10,P(id).Warehouse);
        var bad=Path.Combine(root,"bad.stockbackup"); File.WriteAllText(bad,"invalid"); Assert.Throws<BusinessException>(()=>service.Restore(bad)); Assert.Equal(10,P(id).Warehouse);
        service.ValidateIntegrity();
    }
    [Fact] public void A21_UnsupportedSchemaDoesNotResetData()
    {
        var id=New(); using(var cn=new SqliteConnection($"Data Source={service.DatabasePath};Pooling=False")) { cn.Open(); using var cmd=cn.CreateCommand(); cmd.CommandText="PRAGMA user_version=99"; cmd.ExecuteNonQuery(); }
        Assert.Throws<BusinessException>(()=>new StockService(service.DataDirectory)); Assert.Equal(13,P(id).Total);
    }
    [Fact] public void A22_SnapshotsIdentityAndDisable()
    {
        var id=New(); var doc=Buy(id,1); Assert.Throws<BusinessException>(()=>service.UpdateProduct(id,"新螺丝","M8",false));service.CompleteProduct(id,ProductType.Accessory,"新螺丝","M8","","",false);
        Assert.Equal("螺丝",service.GetDocument(doc).Lines.Single().Name); Assert.Empty(service.Products()); Assert.Throws<BusinessException>(()=>Buy(id,1));
        Assert.Throws<BusinessException>(()=>service.CreateProduct(" 新螺丝 ","Ｍ８","件",0,0,Key()));
    }
    [Fact] public void ChannelValidationAndReopen()
    {
        var id=New(); Assert.Throws<BusinessException>(()=>service.Commit(DocumentKind.Sale,"厂家",[new(id,1)],Key()));
        Buy(id,2); service=new(service.DataDirectory,clock); Assert.Equal(12,P(id).Warehouse); service.ValidateIntegrity();
    }
    public void Dispose() { if(Directory.Exists(root))Directory.Delete(root,true); }
}

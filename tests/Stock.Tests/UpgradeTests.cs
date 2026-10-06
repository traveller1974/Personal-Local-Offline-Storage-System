using System.IO.Compression;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Stock.Core;
using Xunit;

namespace Stock.Tests;

public sealed class UpgradeTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"stock-v2-"+Guid.NewGuid().ToString("N"));
    private readonly TestClock clock=new();
    private string Data=>Path.Combine(root,"Data");
    private static string Key()=>Guid.NewGuid().ToString("N");
    private void V1()
    {
        Directory.CreateDirectory(Path.Combine(Data,"photos"));File.WriteAllBytes(Path.Combine(Data,"photos","v1.png"),[1,2,3]);
        using var cn=new SqliteConnection($"Data Source={Path.Combine(Data,"stock.db")};Pooling=False");cn.Open();using var cmd=cn.CreateCommand();cmd.CommandText=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","v1.sql"));cmd.ExecuteNonQuery();
        cmd.CommandText="""
            INSERT INTO Product VALUES(42,'旧货品','48V','辆','旧货品','48V',1);
            INSERT INTO StockBalance VALUES(42,15,3);INSERT INTO CarryForward VALUES(42,0,0);
            INSERT INTO Document(Id,Number,Kind,Channel,Status,BusinessDate,OccurredAt,SubmissionKey) VALUES
             ('opening','N0','Opening','期初','Valid','2026-10-05','2026-10-05T10:00:00+08:00','k0'),
             ('buy','N1','Purchase','厂家','Valid','2026-10-05','2026-10-05T11:00:00+08:00','k1');
            INSERT INTO DocumentLine VALUES(100,'opening',42,'历史原名','48V','辆',13,10,3),(120,'buy',42,'旧货品','48V','辆',3,3,0),(130,'buy',42,'旧货品','48V','辆',2,2,0);
            INSERT INTO Attachment VALUES(1,'buy','photos/v1.png');
            """;cmd.ExecuteNonQuery();
    }
    [Fact] public void MigrationPreservesIdsBalancesLineOrderAndUnknownSnapshots()
    {
        V1();var service=new StockService(Data,clock);var p=service.GetProduct(42);Assert.Equal((15L,3L),(p.Warehouse,p.Store));Assert.False(p.Complete);Assert.Null(p.Color);Assert.Equal(ProductType.Unknown,p.Type);
        Assert.Equal(new[]{1,2},service.GetDocument("buy").Lines.Select(l=>l.LineOrder));
        Assert.Equal("未记录",service.GetDocument("buy").Lines[0].TypeText);
        Assert.Equal("历史原名",service.GetDocument("opening").Lines[0].Name);
        service.CompleteProduct(42,ProductType.Vehicle,"车","48V","白","20003",true);
        Assert.Null(service.GetDocument("buy").Lines[0].Color);Assert.Equal(ProductType.Unknown,service.GetDocument("buy").Lines[0].Type);
        Assert.True(File.Exists(service.AttachmentPath("photos/v1.png")));Assert.Single(Directory.GetFiles(Path.Combine(root,"ProtectionBackups"),"*.stockbackup"));service.ValidateIntegrity();
    }
    [Fact] public void InterruptedMigrationRollsBackAndLeavesRestorableV1Backup()
    {
        V1();Assert.Throws<IOException>(()=>new StockService(Data,clock,stage=>{if(stage=="BeforeMigrationCommit")throw new IOException("injected");}));
        using(var cn=new SqliteConnection($"Data Source={Path.Combine(Data,"stock.db")};Pooling=False")){cn.Open();using var cmd=cn.CreateCommand();cmd.CommandText="PRAGMA user_version";Assert.Equal(1L,cmd.ExecuteScalar());cmd.CommandText="SELECT Warehouse FROM StockBalance WHERE ProductId=42";Assert.Equal(15L,cmd.ExecuteScalar());}
        var backup=Directory.GetFiles(Path.Combine(root,"ProtectionBackups"),"*.stockbackup").Single();var service=new StockService(Path.Combine(root,"Current"),clock);service.CreateProduct("临时","","件",1,0,Key());service.Restore(backup);
        Assert.Equal(18,service.GetProduct(42).Total);Assert.Equal(2,service.GetDocument("buy").Lines.Count);service.ValidateIntegrity();
    }
    [Fact] public void PreciseIdentityAndMarkerSnapshotsRemainSeparateAndVoidAggregates()
    {
        var s=new StockService(Data,clock);long New(string color,string code,string spec="48V/20Ah")=>s.CreateProduct(ProductType.Vehicle,"车",spec,color,code,0,0,Key());
        var a=New("白","123");var b=New("黑","123");var c=New("白","124");var d=New("白","","48V/20Ah");var e=New("白","123","48Ｖ/20Ah");
        Assert.Equal(5,s.Products().Count);Assert.Throws<BusinessException>(()=>New("白","123"));
        s.UpdateProduct(e,"车","48Ｖ/20Ah",false);Assert.False(s.GetProduct(e).Active);Assert.Equal("48Ｖ/20Ah",s.GetProduct(e).Spec);
        var id=s.Commit(DocumentKind.Purchase,"厂家",[new(a,3,1,"1","X1","PC"),new(a,4,1,"2","A","PC"),new(a,0,1,"3","A","PC"),new(b,2)],Key());
        var doc=s.GetDocument(id);Assert.Equal(new long[]{3,4,0,2},doc.Lines.Select(l=>l.Quantity));Assert.Equal(new[]{"X1","A","A",""},doc.Lines.Select(l=>l.Marker));Assert.Equal(7,s.GetProduct(a).Total);s.Void(id,"整单重复行作废",Key());Assert.Equal(0,s.GetProduct(a).Total);Assert.Equal(4,s.GetDocument(s.GetDocument(id).VoidId!).Lines.Count);s.ValidateIntegrity();
        s.Commit(DocumentKind.Purchase,"厂家",[new(a,3),new(a,4)],Key());s.Commit(DocumentKind.Sale,"零售",[new(a,3),new(a,4)],Key());Assert.Equal(0,s.GetProduct(a).Total);s.ValidateIntegrity();
    }
    [Fact] public void IdentityCorrectionConflictDoesNotMergeOrChangeBalances()
    {
        var s=new StockService(Data,clock);var a=s.CreateProduct(ProductType.Battery,"电池","72V","","1",2,0,Key());var b=s.CreateProduct(ProductType.Battery,"电池","72V","","2",3,0,Key());
        Assert.Throws<BusinessException>(()=>s.CompleteProduct(b,ProductType.Battery,"电池","72V","","1",true));Assert.Equal(3,s.GetProduct(b).Total);Assert.Equal("2",s.GetProduct(b).MaterialCode);s.ValidateIntegrity();
    }
    [Fact] public void SqlFiltersSelectOnlyMatchingLinesAndSummariesSeparateUnits()
    {
        var s=new StockService(Data,clock);var a=s.CreateProduct(ProductType.Vehicle,"同名","A","白","1",0,0,Key());var b=s.CreateProduct(ProductType.Vehicle,"同名","B","黑","2",0,0,Key());var c=s.CreateProduct(ProductType.Charger,"同名","A","","1",0,0,Key());
        var id=s.Commit(DocumentKind.Purchase,"厂家",[new(a,2),new(b,3),new(c,4)],Key());
        var selection=new ProductFilter([ProductType.Vehicle],["同名"],["A","B"],["白"],"1");Assert.Single(s.ProductPage(selection).Items);
        var filter=new QueryFilter(clock.Today,clock.Today,Products:selection);Assert.Single(s.QueryPage(filter).Items[0].Lines);Assert.Equal(3,s.GetDocument(id).Lines.Count);
        var summaries=s.Summarize(new(false,false,false));Assert.Equal(2,summaries.Count);Assert.Equal(new[]{"个","辆"},summaries.Select(r=>r.Unit).Order().ToArray());
        var output=Path.Combine(root,"selection.xlsx");ExcelExporter.Export(s,filter,output,new(["货品名称","类型","颜色","物料编码","数量","行序"]));using var book=new XLWorkbook(output);Assert.Equal(2,book.Worksheet("明细").LastRowUsed()!.RowNumber());Assert.Equal("白",book.Worksheet("明细").Cell(2,3).GetString());
        s.Void(id,"测试",Key());Assert.Empty(s.Summarize(new(),ledger:new(clock.Today,clock.Today,Status:StatusFilter.All)));
    }
    [Fact] public void RetentionCreatesFullProtectionAndKeepsZeroReferencedProducts()
    {
        clock.Today=new(2023,10,4);var s=new StockService(Data,clock);var unused=s.CreateProduct(ProductType.Accessory,"不用","","","",0,0,Key());var nonzero=s.CreateProduct(ProductType.Accessory,"在库","","","",4,0,Key());
        clock.Today=new(2023,10,5);var referenced=s.CreateProduct(ProductType.Accessory,"保留","","","",0,0,Key());clock.Today=new(2026,10,5);s.Maintain();
        Assert.Throws<BusinessException>(()=>s.GetProduct(unused));Assert.Equal(4,s.GetProduct(nonzero).Total);Assert.Equal(0,s.GetProduct(referenced).Total);Assert.Equal(new DateOnly(2023,10,5),s.Cutoff);
        var protection=Directory.GetFiles(Path.Combine(root,"ProtectionBackups"),"*.stockbackup").Single();using var zip=ZipFile.OpenRead(protection);Assert.NotNull(zip.GetEntry("stock.db"));s.ValidateIntegrity();clock.Today=new(2025,1,1);Assert.Equal(new DateOnly(2023,10,5),s.Maintain().Cutoff);
    }
    [Fact] public void CancelledExportLeavesExistingFileAndNoPartialWorkbook()
    {
        var s=new StockService(Data,clock);var output=Path.Combine(root,"existing.xlsx");File.WriteAllText(output,"keep");using var cts=new CancellationTokenSource();cts.Cancel();
        Assert.Throws<OperationCanceledException>(()=>ExcelExporter.ExportInventory(s,null,"",output,token:cts.Token));Assert.Equal("keep",File.ReadAllText(output));Assert.Empty(Directory.GetFiles(root,"*.tmp.xlsx"));
    }
    [Fact] public void QueryOrderPreservesRealTimeAcrossDifferentOffsets()
    {
        var s=new StockService(Data,clock);var p=s.CreateProduct(ProductType.Accessory,"附件","","","",0,0,Key());
        var first=s.Commit(DocumentKind.Purchase,"厂家",[new(p,1)],Key());var later=s.Commit(DocumentKind.Purchase,"厂家",[new(p,2)],Key());
        using(var cn=new SqliteConnection($"Data Source={s.DatabasePath};Pooling=False")){cn.Open();using var cmd=cn.CreateCommand();cmd.CommandText="UPDATE Document SET OccurredAt=CASE Id WHEN $first THEN '2026-10-05T10:00:00+08:00' ELSE '2026-10-05T07:00:00-07:00' END WHERE Id IN($first,$later)";cmd.Parameters.AddWithValue("$first",first);cmd.Parameters.AddWithValue("$later",later);cmd.ExecuteNonQuery();}
        var filter=new QueryFilter(clock.Today,clock.Today);Assert.Equal(later,s.QueryPage(filter).Items[0].Id);Assert.Equal(2,s.Ledger(filter).First().Line.Quantity);
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}

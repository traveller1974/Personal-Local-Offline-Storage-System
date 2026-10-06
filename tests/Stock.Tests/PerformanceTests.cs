using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using Microsoft.Data.Sqlite;
using Stock.Core;
using Xunit;

namespace Stock.Tests;

public sealed class PerformanceTests
{
    [Fact, Trait("Category","Performance")]
    public void TwoHundredThousandRowsQuerySummaryPaginationFullExportAndCancellation()
    {
        var repo=new DirectoryInfo(AppContext.BaseDirectory);while(repo!=null&&!File.Exists(Path.Combine(repo.FullName,"LocalStockManager.slnx")))repo=repo.Parent;
        var output=Path.Combine(repo!.FullName,"artifacts","v1.1","performance");Directory.CreateDirectory(output);var data=Path.Combine(output,"run-"+Guid.NewGuid().ToString("N"));
        var clock=new TestClock();var service=new StockService(data,clock);
        using(var cn=new SqliteConnection($"Data Source={service.DatabasePath};Pooling=False"))
        {
            cn.Open();using var tx=cn.BeginTransaction();using var cmd=cn.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="""
                WITH RECURSIVE n(x) AS(VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<1000)
                INSERT INTO Product(Id,Name,Spec,Unit,NameKey,SpecKey,Active,Type,Color,MaterialCode,Complete,ColorKey,CodeKey,LastUsed)
                SELECT x,'货'||(x%10),'S'||(x%20),CASE x%4 WHEN 0 THEN '辆' WHEN 1 THEN '组' WHEN 2 THEN '个' ELSE '件' END,
                '货'||(x%10),'S'||(x%20),1,CASE x%4 WHEN 0 THEN 'Vehicle' WHEN 1 THEN 'Battery' WHEN 2 THEN 'Charger' ELSE 'Accessory' END,
                CASE WHEN x%4 IN(0,3) THEN 'C'||(x%3) ELSE '' END,printf('%06d',x),1,
                CASE WHEN x%4 IN(0,3) THEN 'C'||(x%3) ELSE '' END,printf('%06d',x),'2026-10-05' FROM n;
                INSERT INTO StockBalance SELECT Id,0,0 FROM Product;INSERT INTO CarryForward SELECT Id,0,0 FROM Product;
                WITH RECURSIVE n(x) AS(VALUES(0) UNION ALL SELECT x+1 FROM n WHERE x<9999)
                INSERT INTO Document(Id,Number,Kind,Channel,Status,BusinessDate,OccurredAt,SubmissionKey)
                SELECT 'd'||x,'N'||x,'Purchase','厂家','Valid','2026-10-05','2026-10-05T12:00:00+08:00','key'||x FROM n;
                WITH RECURSIVE n(x) AS(VALUES(0) UNION ALL SELECT x+1 FROM n WHERE x<199999)
                INSERT INTO DocumentLine(DocumentId,ProductId,Name,Spec,Unit,Quantity,WarehouseDelta,StoreDelta,LineOrder,Type,Color,MaterialCode,Marker,RawUnit)
                SELECT 'd'||(x/20),p.Id,p.Name,p.Spec,p.Unit,1,1,0,x%20+1,p.Type,p.Color,p.MaterialCode,'A',
                CASE p.Type WHEN 'Vehicle' THEN 'PC' WHEN 'Battery' THEN 'PAA' WHEN 'Charger' THEN 'PC' ELSE 'Z1' END
                FROM n JOIN Product p ON p.Id=x%1000+1;
                UPDATE StockBalance SET Warehouse=200;
                """;cmd.ExecuteNonQuery();tx.Commit();
        }
        service.ValidateIntegrity();var filter=new QueryFilter(clock.Today,clock.Today,Products:new([ProductType.Vehicle],["货0","货4"],["S0","S4"],["C0","C1"]));
        service.QueryPage(filter);service.Summarize(new(true,false,true),ledger:filter);
        var watch=Stopwatch.StartNew();var page=service.QueryPage(filter,2);var queryMs=watch.Elapsed.TotalMilliseconds;Assert.NotEmpty(page.Items);Assert.True(page.Items.All(d=>d.Lines.All(l=>l.Type==ProductType.Vehicle&&l.Color is "C0" or "C1")));Assert.True(queryMs<3000,$"query {queryMs}ms");
        watch.Restart();var groups=service.Summarize(new(true,false,true),ledger:filter);var summaryMs=watch.Elapsed.TotalMilliseconds;Assert.NotEmpty(groups);Assert.True(summaryMs<3000,$"summary {summaryMs}ms");
        var all=new QueryFilter(clock.Today,clock.Today);var destination=Path.Combine(output,"200000-lines.xlsx");watch.Restart();ExcelExporter.Export(service,all,destination);var exportMs=watch.Elapsed.TotalMilliseconds;Assert.True(exportMs<120000,$"export {exportMs}ms");
        long rows=0;using(var zip=ZipFile.OpenRead(destination))using(var stream=zip.GetEntry("xl/worksheets/sheet1.xml")!.Open())using(var reader=XmlReader.Create(stream))while(reader.Read())if(reader.NodeType==XmlNodeType.Element&&reader.LocalName=="row")rows++;Assert.Equal(200001,rows);
        var cancelled=Path.Combine(output,"cancelled.xlsx");File.WriteAllText(cancelled,"preserve");using(var cts=new CancellationTokenSource(TimeSpan.FromMilliseconds(30)))Assert.ThrowsAny<OperationCanceledException>(()=>ExcelExporter.Export(service,all,cancelled,token:cts.Token));Assert.Equal("preserve",File.ReadAllText(cancelled));Assert.Empty(Directory.GetFiles(output,"*.tmp.xlsx"));File.Delete(cancelled);
        File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new{success=true,rows=200000,documents=10000,products=1000,queryMs,summaryMs,exportMs,exportRows=rows-1,cancellationVerified=true,processorCount=Environment.ProcessorCount,os=Environment.OSVersion.ToString(),runtime=Environment.Version.ToString(),database=service.DatabasePath},new JsonSerializerOptions{WriteIndented=true}));
    }
}

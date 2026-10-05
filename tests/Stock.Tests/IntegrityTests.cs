using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Stock.Core;
using Xunit;

namespace Stock.Tests;

public class IntegrityTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"stock-integrity-"+Guid.NewGuid().ToString("N"));
    private readonly TestClock clock=new();
    private readonly StockService service;
    private static string Key()=>Guid.NewGuid().ToString("N");
    public IntegrityTests()=>service=new(Path.Combine(root,"Data"),clock);
    [Fact] public void ClockRollbackCannotWriteAlreadyDeletedPeriod()
    {var id=service.CreateProduct("螺丝","M8","件",10,0,Key());service.Maintain();clock.Today=new(2020,1,1);Assert.Throws<BusinessException>(()=>service.Commit(DocumentKind.Purchase,"厂家",[new(id,1)],Key()));Assert.Equal(10,service.Products().Single().Total);}
    [Fact] public void MissingBalanceCannotPassIntegrityCheck()
    {var id=service.CreateProduct("螺丝","M8","件",0,0,Key());using(var c=new SqliteConnection($"Data Source={service.DatabasePath};Pooling=False")){c.Open();using var cmd=c.CreateCommand();cmd.CommandText="DELETE FROM StockBalance";cmd.ExecuteNonQuery();}Assert.Throws<BusinessException>(service.ValidateIntegrity);}
    [Fact] public async Task ConcurrentIndependentServicesCannotOversell()
    {
        var id=service.CreateProduct("螺丝","M8","件",10,0,Key());var other=new StockService(service.DataDirectory,clock);
        async Task<bool> Sell(StockService s)=>await Task.Run(()=>{try{s.Commit(DocumentKind.Sale,"零售",[new(id,7)],Key());return true;}catch(BusinessException){return false;}});
        var results=await Task.WhenAll(Sell(service),Sell(other));Assert.Single(results,x=>x);Assert.Equal(3,service.Products().Single().Warehouse);service.ValidateIntegrity();
    }
    [Fact] public void RestoreRejectsZipTraversalBeforeChangingCurrentData()
    {
        service.CreateProduct("螺丝","M8","件",10,0,Key());var bad=Path.Combine(root,"malicious.stockbackup");using(var z=ZipFile.Open(bad,ZipArchiveMode.Create)){using var w=new StreamWriter(z.CreateEntry("../outside.txt").Open());w.Write("outside");}
        Assert.Throws<BusinessException>(()=>service.Restore(bad));Assert.Equal(10,service.Products().Single().Warehouse);Assert.False(File.Exists(Path.Combine(root,"outside.txt")));service.ValidateIntegrity();
    }
    [Fact] public void RestoreRejectsIncompatibleDatabaseEvenWithValidFileChecksum()
    {
        var id=service.CreateProduct("螺丝","M8","件",10,0,Key());var backup=Path.Combine(root,"base.stockbackup");service.Backup(backup);
        service.Commit(DocumentKind.Purchase,"厂家",[new(id,1)],Key());var staging=Path.Combine(root,"incompatible");ZipFile.ExtractToDirectory(backup,staging);
        var database=Path.Combine(staging,"stock.db");using(var c=new SqliteConnection($"Data Source={database};Pooling=False")){c.Open();using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA user_version=99";cmd.ExecuteNonQuery();}
        var manifestFile=Path.Combine(staging,"manifest.json");var manifest=JsonNode.Parse(File.ReadAllText(manifestFile))!;manifest["Files"]!["stock.db"]=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(database)));File.WriteAllText(manifestFile,manifest.ToJsonString());
        var changed=Path.Combine(root,"incompatible.stockbackup");ZipFile.CreateFromDirectory(staging,changed);
        Assert.Throws<BusinessException>(()=>service.Restore(changed));Assert.Equal(11,service.Products().Single().Warehouse);service.ValidateIntegrity();
    }
    [Fact] public void BackupPreservesPhotoAndRestoreRejectsMissingReferencedPhoto()
    {
        var id=service.CreateProduct("螺丝","M8","件",10,0,Key());var photo=Path.Combine(root,"照片.png");File.WriteAllBytes(photo,Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/r3sAAAAASUVORK5CYII="));
        var doc=service.Commit(DocumentKind.Purchase,"厂家",[new(id,5)],Key(),[photo]);var backup=Path.Combine(root,"photo.stockbackup");service.Backup(backup);service.Restore(backup);
        var relative=Assert.Single(service.GetDocument(doc).Attachments);Assert.Equal(File.ReadAllBytes(photo),File.ReadAllBytes(service.AttachmentPath(relative)));
        service.Commit(DocumentKind.Sale,"零售",[new(id,2)],Key());var staging=Path.Combine(root,"missing-photo");ZipFile.ExtractToDirectory(backup,staging);File.Delete(Path.Combine(staging,relative));
        var manifestFile=Path.Combine(staging,"manifest.json");var manifest=JsonNode.Parse(File.ReadAllText(manifestFile))!;((JsonObject)manifest["Files"]!).Remove(relative);File.WriteAllText(manifestFile,manifest.ToJsonString());var changed=Path.Combine(root,"missing-photo.stockbackup");ZipFile.CreateFromDirectory(staging,changed);
        Assert.Throws<BusinessException>(()=>service.Restore(changed));Assert.Equal(13,service.Products().Single().Warehouse);Assert.True(File.Exists(service.AttachmentPath(relative)));service.ValidateIntegrity();
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}

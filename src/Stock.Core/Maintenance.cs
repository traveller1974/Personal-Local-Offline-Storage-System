using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Stock.Core;

public sealed partial class StockService
{
    public void ValidateIntegrity()
    {
        lock(gate)
        {
            using var cn=Connect();
            if((string?)Scalar(cn,null,"PRAGMA integrity_check")!="ok") throw new BusinessException("数据库完整性检查失败，请从备份恢复。原数据未被改写。");
            using(var cmd=Command(cn,null,"PRAGMA foreign_key_check")) using(var r=cmd.ExecuteReader()) if(r.Read()) throw new BusinessException("数据库关联检查失败，请从备份恢复。");
            if(Convert.ToInt64(Scalar(cn,null,"SELECT COUNT(*) FROM Product p LEFT JOIN StockBalance b ON p.Id=b.ProductId LEFT JOIN CarryForward c ON p.Id=c.ProductId WHERE b.ProductId IS NULL OR c.ProductId IS NULL"))!=0)
                throw new BusinessException("货品缺少余额或结转记录，请从备份恢复。");
            var sql="""
                SELECT p.Id,b.Warehouse,b.Store,
                    COALESCE(c.Warehouse,0)+COALESCE(SUM(l.WarehouseDelta),0),
                    COALESCE(c.Store,0)+COALESCE(SUM(l.StoreDelta),0)
                FROM Product p JOIN StockBalance b ON p.Id=b.ProductId
                LEFT JOIN CarryForward c ON p.Id=c.ProductId LEFT JOIN DocumentLine l ON p.Id=l.ProductId
                GROUP BY p.Id,b.Warehouse,b.Store,c.Warehouse,c.Store
                """;
            using var check=Command(cn,null,sql); using var reader=check.ExecuteReader();
            while(reader.Read())
            {
                var w=reader.GetInt64(1); var s=reader.GetInt64(2); Rules.Balance(w,s);
                if(w!=reader.GetInt64(3)||s!=reader.GetInt64(4)) throw new BusinessException("库存余额与流水不一致，请从备份恢复。禁止继续记账。");
            }
        }
    }
    public MaintenanceResult Maintain()
    {
        lock(gate)
        {
            ValidateIntegrity(); var cutoff=clock.Today.AddYears(-5);
            using(var cn=Connect())
            {
                var previous=DateOnly.Parse((string)Scalar(cn,null,"SELECT Value FROM Metadata WHERE Key='Cutoff'")!);
                if(cutoff<previous) cutoff=previous; // Clock rollback must not reopen an already deleted period.
                using var tx=cn.BeginTransaction();
                var sums=new List<(long ProductId,long W,long S)>();
                using(var cmd=Command(cn,tx,"SELECT l.ProductId,SUM(l.WarehouseDelta),SUM(l.StoreDelta) FROM DocumentLine l JOIN Document d ON d.Id=l.DocumentId WHERE d.BusinessDate<$c GROUP BY l.ProductId",("$c",Rules.DateText(cutoff))))
                using(var r=cmd.ExecuteReader()) { while(r.Read()) sums.Add((r.GetInt64(0),r.GetInt64(1),r.GetInt64(2))); }
                foreach(var row in sums) Run(cn,tx,"UPDATE CarryForward SET Warehouse=Warehouse+$w,Store=Store+$s WHERE ProductId=$p",("$w",row.W),("$s",row.S),("$p",row.ProductId));
                var deleted=Run(cn,tx,"DELETE FROM Document WHERE BusinessDate<$c",("$c",Rules.DateText(cutoff)));
                Run(cn,tx,"UPDATE Metadata SET Value=$v WHERE Key='Cutoff'",("$v",Rules.DateText(cutoff)));
                FaultInjector?.Invoke("BeforeMaintenanceCommit");
                // Check the resulting ledger before commit so any error rolls back the deletion.
                var mismatch=Convert.ToInt64(Scalar(cn,tx,"""
                    SELECT COUNT(*) FROM StockBalance b JOIN CarryForward c ON c.ProductId=b.ProductId
                    WHERE b.Warehouse<>c.Warehouse+COALESCE((SELECT SUM(WarehouseDelta) FROM DocumentLine WHERE ProductId=b.ProductId),0)
                    OR b.Store<>c.Store+COALESCE((SELECT SUM(StoreDelta) FROM DocumentLine WHERE ProductId=b.ProductId),0)
                    """));
                if(mismatch!=0) throw new BusinessException("结转校验失败，清理已回滚。");
                tx.Commit();
                var count=CleanupPhotos(cn);
                return new(cutoff,deleted,count);
            }
        }
    }
    private int CleanupPhotos(SqliteConnection cn)
    {
        var used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using(var cmd=Command(cn,null,"SELECT Path FROM Attachment")) using(var r=cmd.ExecuteReader()) { while(r.Read()) used.Add(ManagedPath(r.GetString(0))); }
        var count=0;
        foreach(var file in Directory.EnumerateFiles(Path.Combine(DataDirectory,"photos")))
        {
            if(used.Contains(file))continue;
            try { File.Delete(file); count++; }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { LogMaintenance($"图片清理将在下次重试：{Path.GetFileName(file)}，{ex.Message}"); }
        }
        // Reclaim deleted rows from the SQLite file without changing balances.
        Run(cn,null,"PRAGMA wal_checkpoint(TRUNCATE);");
        return count;
    }
    private void LogMaintenance(string message)
    {
        try { File.AppendAllText(Path.Combine(DataDirectory,"maintenance.log"),$"{clock.Now:O} {message}{Environment.NewLine}"); } catch { }
    }
    private sealed record BackupManifest(int SchemaVersion, Dictionary<string,string> Files);
    public void Backup(string destination)
    {
        lock(gate)
        {
            ValidateIntegrity(); destination=Path.GetFullPath(destination);
            if(destination.StartsWith(DataDirectory+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new BusinessException("备份请保存在应用数据目录之外。");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temp=Path.Combine(Path.GetDirectoryName(DataDirectory)!,"Temp","backup-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
            var pending=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using(var source=Connect()) using(var target=new SqliteConnection($"Data Source={Path.Combine(temp,"stock.db")};Pooling=False"))
                {
                    target.Open(); source.BackupDatabase(target);
                    using var cmd=Command(source,null,"SELECT Path FROM Attachment"); using var r=cmd.ExecuteReader();
                    while(r.Read())
                    {
                        var relative=r.GetString(0); var dest=Path.Combine(temp,relative); Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(ManagedPath(relative),dest);
                    }
                }
                var files=Directory.GetFiles(temp,"*",SearchOption.AllDirectories).ToDictionary(f=>Path.GetRelativePath(temp,f).Replace('\\','/'),Hash);
                File.WriteAllText(Path.Combine(temp,"manifest.json"),JsonSerializer.Serialize(new BackupManifest(1,files)));
                ZipFile.CreateFromDirectory(temp,pending,CompressionLevel.Optimal,false); File.Move(pending,destination,true);
            }
            finally { if(File.Exists(pending))File.Delete(pending); Directory.Delete(temp,true); }
        }
    }
    private static string Hash(string path) { using var stream=File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    public void Restore(string archive)
    {
        lock(gate)
        {
            var parent=Path.GetDirectoryName(DataDirectory)!; var staging=Path.Combine(parent,"restore-"+Guid.NewGuid().ToString("N")); var rollback=Path.Combine(parent,"rollback-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging); var swapped=false;
            try
            {
                using(var zip=ZipFile.OpenRead(archive))
                {
                    foreach(var entry in zip.Entries)
                    {
                        var path=Path.GetFullPath(Path.Combine(staging,entry.FullName.Replace('/',Path.DirectorySeparatorChar)));
                        if(!path.StartsWith(staging+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||Path.IsPathRooted(entry.FullName)) throw new BusinessException("备份中包含越界文件路径，已拒绝恢复。");
                        if(entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!); entry.ExtractToFile(path,false);
                    }
                }
                if(!File.Exists(Path.Combine(staging,"stock.db"))||!File.Exists(Path.Combine(staging,"manifest.json")))throw new BusinessException("备份缺少数据库或清单。");
                var manifest=JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(staging,"manifest.json"))) ?? throw new BusinessException("备份清单无效。");
                if(manifest.SchemaVersion!=1)throw new BusinessException("备份版本不兼容。");
                var actual=Directory.GetFiles(staging,"*",SearchOption.AllDirectories).Where(f=>f!=Path.Combine(staging,"manifest.json")).ToDictionary(f=>Path.GetRelativePath(staging,f).Replace('\\','/'),Hash);
                if(actual.Count!=manifest.Files.Count||actual.Any(k=>!manifest.Files.TryGetValue(k.Key,out var hash)||hash!=k.Value)) throw new BusinessException("备份校验失败，文件损坏或被修改。");
                using(var source=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(staging,"stock.db"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString()))
                {source.Open();if(Convert.ToInt32(Scalar(source,null,"PRAGMA user_version"))!=1)throw new BusinessException("备份数据库结构版本不兼容。");}
                var candidate=new StockService(staging,clock); candidate.ValidateIntegrity();
                using(var cn=candidate.Connect()) using(var cmd=Command(cn,null,"SELECT Path FROM Attachment")) using(var r=cmd.ExecuteReader())
                    while(r.Read()) if(!File.Exists(candidate.ManagedPath(r.GetString(0))))throw new BusinessException("备份缺少货单照片。");
                candidate.Maintain(); File.Delete(Path.Combine(staging,"manifest.json"));
                Directory.Move(DataDirectory,rollback);
                try { Directory.Move(staging,DataDirectory); swapped=true; }
                catch { Directory.Move(rollback,DataDirectory); throw; }
                ValidateIntegrity();
                // Successful restore no longer needs the temporary old dataset.
                try{Directory.Delete(rollback,true);}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){LogMaintenance("恢复成功，旧数据临时目录清理失败："+ex.Message);}
            }
            catch(Exception ex)
            {
                if(swapped&&Directory.Exists(rollback))
                { Directory.Move(DataDirectory,staging); Directory.Move(rollback,DataDirectory); }
                if(ex is BusinessException)throw;
                throw new BusinessException("恢复失败，原数据已保留。"+ex.Message);
            }
            finally { if(Directory.Exists(staging))Directory.Delete(staging,true); }
        }
    }
}

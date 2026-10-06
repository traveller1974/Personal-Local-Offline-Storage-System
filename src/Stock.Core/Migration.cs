using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Stock.Core;

public sealed partial class StockService
{
    // Keep the original IDs and historic snapshots. Never infer identity from old names.
    private void MigrateV2(SqliteConnection cn, bool protect)
    {
        if (protect) ProtectionBackup(cn, "before-v2");
        Run(cn, null, "PRAGMA foreign_keys=OFF");
        try
        {
            using var tx = cn.BeginTransaction();
            Run(cn, tx, """
                CREATE TABLE ProductV2(Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, Spec TEXT NOT NULL, Unit TEXT NOT NULL,
                  NameKey TEXT NOT NULL, SpecKey TEXT NOT NULL, Active INTEGER NOT NULL CHECK(Active IN(0,1)),
                  Type TEXT NOT NULL DEFAULT 'Unknown', Color TEXT, MaterialCode TEXT, Complete INTEGER NOT NULL DEFAULT 0 CHECK(Complete IN(0,1)),
                  ColorKey TEXT NOT NULL DEFAULT '', CodeKey TEXT NOT NULL DEFAULT '', LastUsed TEXT NOT NULL);
                INSERT INTO ProductV2(Id,Name,Spec,Unit,NameKey,SpecKey,Active,LastUsed)
                  SELECT Id,Name,Spec,Unit,NameKey,SpecKey,Active,$today FROM Product;
                DROP TABLE Product;
                ALTER TABLE ProductV2 RENAME TO Product;
                CREATE UNIQUE INDEX UX_Product_Identity ON Product(Type,NameKey,SpecKey,ColorKey,CodeKey) WHERE Complete=1;
                CREATE UNIQUE INDEX UX_Product_Legacy ON Product(NameKey,SpecKey) WHERE Complete=0;
                ALTER TABLE DocumentLine ADD COLUMN LineOrder INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE DocumentLine ADD COLUMN PhotoOrder INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE DocumentLine ADD COLUMN OriginalOrder TEXT NOT NULL DEFAULT '';
                ALTER TABLE DocumentLine ADD COLUMN Type TEXT NOT NULL DEFAULT 'Unknown';
                ALTER TABLE DocumentLine ADD COLUMN Color TEXT;
                ALTER TABLE DocumentLine ADD COLUMN MaterialCode TEXT;
                ALTER TABLE DocumentLine ADD COLUMN Marker TEXT NOT NULL DEFAULT '';
                ALTER TABLE DocumentLine ADD COLUMN RawUnit TEXT NOT NULL DEFAULT '';
                ALTER TABLE DocumentLine ADD COLUMN RawName TEXT NOT NULL DEFAULT '';
                ALTER TABLE DocumentLine ADD COLUMN ReviewNote TEXT NOT NULL DEFAULT '';
                ALTER TABLE Attachment ADD COLUMN Metadata TEXT NOT NULL DEFAULT '';
                CREATE TEMP TABLE LegacyLineOrder(Id INTEGER PRIMARY KEY, Number INTEGER NOT NULL);
                INSERT INTO LegacyLineOrder SELECT Id,ROW_NUMBER() OVER(PARTITION BY DocumentId ORDER BY Id) FROM DocumentLine;
                UPDATE DocumentLine SET LineOrder=(SELECT Number FROM LegacyLineOrder x WHERE x.Id=DocumentLine.Id);
                DROP TABLE LegacyLineOrder;
                CREATE UNIQUE INDEX UX_Line_Order ON DocumentLine(DocumentId,LineOrder);
                CREATE INDEX IX_Product_Filter ON Product(Type,Name,Spec,Color,MaterialCode);
                CREATE INDEX IX_Line_Filter ON DocumentLine(Type,Name,Spec,Color,MaterialCode,DocumentId);
                CREATE INDEX IX_Document_Query ON Document(BusinessDate,Kind,Status,OccurredAt,Id);
                INSERT OR REPLACE INTO Metadata VALUES('RetentionYears','5');
                PRAGMA user_version=2;
                """, ("$today", Rules.DateText(clock.Today)));
            FaultInjector?.Invoke("BeforeMigrationCommit");
            using(var check=Command(cn,tx,"PRAGMA foreign_key_check")) using(var r=check.ExecuteReader())
                if(r.Read()) throw new BusinessException("迁移关联检查失败，已回滚。");
            tx.Commit();
        }
        finally { Run(cn, null, "PRAGMA foreign_keys=ON"); }
    }

    private void ProtectionBackup(SqliteConnection source, string label)
    {
        var folder=Path.Combine(Path.GetDirectoryName(DataDirectory)!,"ProtectionBackups"); Directory.CreateDirectory(folder);
        var temp=Path.Combine(folder,"temp-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        try
        {
            using(var target=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(temp,"stock.db"),Pooling=false}.ToString()))
            { target.Open(); source.BackupDatabase(target); }
            using(var cmd=Command(source,null,"SELECT Path FROM Attachment")) using(var r=cmd.ExecuteReader())
                while(r.Read()) { var relative=r.GetString(0);var target=Path.Combine(temp,relative);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(ManagedPath(relative),target); }
            var files=Directory.GetFiles(temp,"*",SearchOption.AllDirectories).ToDictionary(f=>Path.GetRelativePath(temp,f).Replace('\\','/'),Hash);
            var version=Convert.ToInt32(Scalar(source,null,"PRAGMA user_version"));
            File.WriteAllText(Path.Combine(temp,"manifest.json"),JsonSerializer.Serialize(new BackupManifest(version,files)));
            ZipFile.CreateFromDirectory(temp,Path.Combine(folder,$"{clock.Now:yyyyMMdd-HHmmss}-{label}-{Guid.NewGuid():N}.stockbackup"));
        }
        finally { Directory.Delete(temp,true); }
    }

    public void CompleteProduct(long id, ProductType type, string name, string spec, string color, string code, bool active)
    {
        CheckIdentity(type,name,spec,color,code);
        lock(gate)
        {
            using var cn=Connect();using var tx=cn.BeginTransaction();_ = ProductById(cn,tx,id);
            try { Run(cn,tx,"""
                UPDATE Product SET Name=$n,Spec=$s,Unit=$u,NameKey=$nk,SpecKey=$sk,Type=$t,Color=$c,MaterialCode=$m,
                ColorKey=$ck,CodeKey=$mk,Complete=1,Active=$a,LastUsed=$date WHERE Id=$id
                """,("$n",name.Trim()),("$s",spec.Trim()),("$u",Rules.Unit(type)),("$nk",Rules.ExactIdentity(name)),("$sk",Rules.ExactIdentity(spec)),
                ("$t",type.ToString()),("$c",color.Trim()),("$m",code.Trim()),("$ck",Rules.ExactIdentity(color)),("$mk",Rules.ExactIdentity(code)),("$a",active?1:0),("$date",Rules.DateText(clock.Today)),("$id",id)); }
            catch(SqliteException ex) when(ex.SqliteErrorCode==19) { throw new BusinessException("完整身份已存在，不能自动合并库存。请核对资料。"); }
            tx.Commit();
        }
    }
    private static void CheckIdentity(ProductType type,string name,string spec,string color,string code)
    {
        _=Rules.Unit(type);
        if(string.IsNullOrWhiteSpace(name)||name.Length>120||spec.Length>500||color.Length>120||code.Length>120) throw new BusinessException("名称必填；名称、颜色、编码最多120字，规格最多500字。");
        if(type is ProductType.Battery or ProductType.Charger && color.Trim().Length>0) throw new BusinessException("电池和充电器颜色必须为空；请核对原单是否列错位。");
    }
    public long CreateProduct(ProductType type,string name,string spec,string color,string code,long warehouse,long store,string key)
    {
        CheckIdentity(type,name,spec,color,code);Rules.Quantity(warehouse,true);Rules.Quantity(store,true);Rules.Balance(warehouse,store);
        lock(gate)
        {
            using var cn=Connect();using var tx=cn.BeginTransaction();var old=ExistingSubmission(cn,tx,key);
            if(old!=null)return Convert.ToInt64(Scalar(cn,tx,"SELECT ProductId FROM DocumentLine WHERE DocumentId=$id LIMIT 1",("$id",old)));
            try { Run(cn,tx,"""
                INSERT INTO Product(Name,Spec,Unit,NameKey,SpecKey,Active,Type,Color,MaterialCode,Complete,ColorKey,CodeKey,LastUsed)
                VALUES($n,$s,$u,$nk,$sk,1,$t,$c,$m,1,$ck,$mk,$d)
                """,("$n",name.Trim()),("$s",spec.Trim()),("$u",Rules.Unit(type)),("$nk",Rules.ExactIdentity(name)),("$sk",Rules.ExactIdentity(spec)),
                ("$t",type.ToString()),("$c",color.Trim()),("$m",code.Trim()),("$ck",Rules.ExactIdentity(color)),("$mk",Rules.ExactIdentity(code)),("$d",Rules.DateText(clock.Today))); }
            catch(SqliteException ex) when(ex.SqliteErrorCode==19) { throw new BusinessException("相同完整身份的货品已存在（可能已停用）。"); }
            var id=Convert.ToInt64(Scalar(cn,tx,"SELECT last_insert_rowid()"));
            Run(cn,tx,"INSERT INTO StockBalance VALUES($p,0,0); INSERT INTO CarryForward VALUES($p,0,0)",("$p",id));
            WriteDocument(cn,tx,DocumentKind.Opening,"期初",[new(ProductById(cn,tx,id),warehouse,store)],key);
            tx.Commit();return id;
        }
    }
}

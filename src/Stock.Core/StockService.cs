using Microsoft.Data.Sqlite;

namespace Stock.Core;

/// <summary>All writers use one gate and an immediate SQLite transaction. No UI may write balances.</summary>
public sealed partial class StockService
{
    public string DataDirectory { get; }
    public string DatabasePath => Path.Combine(DataDirectory, "stock.db");
    private readonly IClock clock;
    private readonly object gate = new();
    public Action<string>? FaultInjector { get; set; }

    public StockService(string directory, IClock? clock = null, Action<string>? faultInjector = null)
    {
        DataDirectory = Path.GetFullPath(directory); this.clock = clock ?? new SystemClock(); FaultInjector=faultInjector;
        Directory.CreateDirectory(DataDirectory); Directory.CreateDirectory(Path.Combine(DataDirectory, "photos"));
        Initialize(); ValidateIntegrity();
    }
    private SqliteConnection Connect()
    {
        var cn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 15 }.ToString());
        cn.Open(); Run(cn, null, "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;"); return cn;
    }
    private static SqliteCommand Command(SqliteConnection cn, SqliteTransaction? tx, string sql, params (string Key, object? Value)[] args)
    {
        var cmd = cn.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    private static int Run(SqliteConnection cn, SqliteTransaction? tx, string sql, params (string Key, object? Value)[] args)
    { using var cmd = Command(cn, tx, sql, args); return cmd.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection cn, SqliteTransaction? tx, string sql, params (string Key, object? Value)[] args)
    { using var cmd = Command(cn, tx, sql, args); return cmd.ExecuteScalar(); }
    private void Initialize()
    {
        using var cn = Connect(); var version = Convert.ToInt32(Scalar(cn, null, "PRAGMA user_version"));
        if (version > 2) throw new BusinessException("数据库版本高于本软件版本，请使用较新版本打开。原数据未被更改。");
        if (version == 2) return;
        if (version == 1) { MigrateV2(cn,true); return; }
        var tables = Convert.ToInt64(Scalar(cn, null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"));
        if (tables > 0) throw new BusinessException("无法识别数据库结构，原数据未被更改。");
        Run(cn, null, "PRAGMA journal_mode=WAL;");
        using var tx = cn.BeginTransaction();
        Run(cn, tx, """
            CREATE TABLE Product(Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, Spec TEXT NOT NULL, Unit TEXT NOT NULL,
                NameKey TEXT NOT NULL, SpecKey TEXT NOT NULL, Active INTEGER NOT NULL CHECK(Active IN(0,1)), UNIQUE(NameKey,SpecKey));
            CREATE TABLE StockBalance(ProductId INTEGER PRIMARY KEY REFERENCES Product(Id), Warehouse INTEGER NOT NULL, Store INTEGER NOT NULL,
                CHECK(Warehouse>=0 AND Store>=0 AND Warehouse+Store<=2147483647));
            CREATE TABLE Document(Id TEXT PRIMARY KEY, Number TEXT NOT NULL UNIQUE, Kind TEXT NOT NULL, Channel TEXT NOT NULL, Status TEXT NOT NULL,
                BusinessDate TEXT NOT NULL, OccurredAt TEXT NOT NULL, SubmissionKey TEXT NOT NULL UNIQUE, OriginalId TEXT, VoidId TEXT, VoidAt TEXT, Reason TEXT NOT NULL DEFAULT '');
            CREATE TABLE DocumentLine(Id INTEGER PRIMARY KEY, DocumentId TEXT NOT NULL REFERENCES Document(Id) ON DELETE CASCADE,
                ProductId INTEGER NOT NULL REFERENCES Product(Id), Name TEXT NOT NULL, Spec TEXT NOT NULL, Unit TEXT NOT NULL, Quantity INTEGER NOT NULL,
                WarehouseDelta INTEGER NOT NULL, StoreDelta INTEGER NOT NULL);
            CREATE TABLE Attachment(Id INTEGER PRIMARY KEY, DocumentId TEXT NOT NULL REFERENCES Document(Id) ON DELETE CASCADE, Path TEXT NOT NULL UNIQUE);
            CREATE TABLE CarryForward(ProductId INTEGER PRIMARY KEY REFERENCES Product(Id), Warehouse INTEGER NOT NULL, Store INTEGER NOT NULL);
            CREATE TABLE Metadata(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            CREATE INDEX IX_Document_Date ON Document(BusinessDate,OccurredAt);
            CREATE INDEX IX_Line_Product ON DocumentLine(ProductId,DocumentId);
            INSERT INTO Metadata VALUES('Cutoff','0001-01-01');
            PRAGMA user_version=1;
            """);
        tx.Commit(); MigrateV2(cn,false);
    }
    private const string ProductColumns="p.Id,p.Name,p.Spec,p.Unit,p.Active,b.Warehouse,b.Store,p.Type,p.Color,p.MaterialCode,p.Complete";
    private static Product ReadProduct(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4) != 0, r.GetInt64(5), r.GetInt64(6),Enum.Parse<ProductType>(r.GetString(7)),r.IsDBNull(8)?null:r.GetString(8),r.IsDBNull(9)?null:r.GetString(9),r.GetInt64(10)!=0);
    private static Product ProductById(SqliteConnection cn, SqliteTransaction? tx, long id)
    {
        using var cmd = Command(cn, tx, $"SELECT {ProductColumns} FROM Product p JOIN StockBalance b ON p.Id=b.ProductId WHERE p.Id=$id", ("$id", id));
        using var r = cmd.ExecuteReader(); if (!r.Read()) throw new BusinessException("货品不存在。"); return ReadProduct(r);
    }
    public IReadOnlyList<Product> Products(string search = "", bool includeInactive = false)
    {
        lock (gate)
        {
            using var cn = Connect(); using var cmd = Command(cn, null, $"SELECT {ProductColumns} FROM Product p JOIN StockBalance b ON p.Id=b.ProductId WHERE ($active=1 OR p.Active=1) AND (instr(lower(p.Name),lower($search))>0 OR instr(lower(p.Spec),lower($search))>0 OR instr(lower(COALESCE(p.MaterialCode,'')),lower($search))>0) ORDER BY p.Name,p.Spec,p.Id",("$active",includeInactive?1:0),("$search",search));
            using var r = cmd.ExecuteReader(); var items = new List<Product>(); while (r.Read()) items.Add(ReadProduct(r));
            return items;
        }
    }
    public long CreateProduct(string name, string spec, string unit, long warehouse, long store, string submissionKey)
    {
        name = Rules.Clean(name); spec = Rules.Clean(spec); unit = Rules.Clean(unit);
        if (name.Length is 0 or > 120 || spec.Length > 120 || unit.Length is 0 or > 20) throw new BusinessException("名称必填且不超过120字；规格不超过120字；单位必填且不超过20字。");
        Rules.Quantity(warehouse, true); Rules.Quantity(store, true); Rules.Balance(warehouse, store);
        lock (gate)
        {
            using var cn = Connect(); using var tx = cn.BeginTransaction();
            var old = ExistingSubmission(cn, tx, submissionKey);
            if (old != null) { var id = Convert.ToInt64(Scalar(cn, tx, "SELECT ProductId FROM DocumentLine WHERE DocumentId=$id LIMIT 1", ("$id", old))); return id; }
            if (Scalar(cn, tx, "SELECT Id FROM Product WHERE NameKey=$n AND SpecKey=$s", ("$n", Rules.Identity(name)), ("$s", Rules.Identity(spec))) != null)
                throw new BusinessException("同名同规格货品已存在（可能已停用），请选择或重新启用。");
            Run(cn, tx, "INSERT INTO Product(Name,Spec,Unit,NameKey,SpecKey,Active,LastUsed) VALUES($n,$s,$u,$nk,$sk,1,$date)", ("$n", name), ("$s", spec), ("$u", unit), ("$nk", Rules.Identity(name)), ("$sk", Rules.Identity(spec)),("$date",Rules.DateText(clock.Today)));
            var productId = Convert.ToInt64(Scalar(cn, tx, "SELECT last_insert_rowid()"));
            Run(cn, tx, "INSERT INTO StockBalance VALUES($id,0,0); INSERT INTO CarryForward VALUES($id,0,0);", ("$id", productId));
            var product = ProductById(cn, tx, productId);
            WriteDocument(cn, tx, DocumentKind.Opening, "期初", [new StockImpact(product, warehouse, store)], submissionKey);
            tx.Commit(); return productId;
        }
    }
    public void UpdateProduct(long id, string name, string spec, bool active)
    {
        if (name.Trim().Length is 0 or >120 || spec.Length>500) throw new BusinessException("名称必填且最多120字；完整规格最多500字。");
        lock(gate)
        {
            using var cn = Connect(); using var tx = cn.BeginTransaction(); var product = ProductById(cn,tx,id);
            name=product.Complete?Rules.ExactIdentity(name):Rules.Clean(name);spec=product.Complete?Rules.ExactIdentity(spec):Rules.Clean(spec);
            if((name!=product.Name||spec!=product.Spec) && (product.Total!=0 || Convert.ToInt64(Scalar(cn,tx,"SELECT COUNT(*) FROM DocumentLine WHERE ProductId=$id",("$id",id)))>0))
                throw new BusinessException("这个货品已有库存或历史单据。请在库存首页点“编辑”，核实并勾选资料修改说明后保存。");
            if ((name!=product.Name||spec!=product.Spec) && Scalar(cn,tx,"SELECT Id FROM Product WHERE NameKey=$n AND SpecKey=$s AND Id<>$id",("$n",Rules.Identity(name)),("$s",Rules.Identity(spec)),("$id",id)) != null)
                throw new BusinessException("同名同规格货品已经存在。");
            Run(cn,tx,"UPDATE Product SET Name=$n,Spec=$s,NameKey=$nk,SpecKey=$sk,Active=$a WHERE Id=$id",("$n",name),("$s",spec),("$nk",product.Complete?Rules.ExactIdentity(name):Rules.Identity(name)),("$sk",product.Complete?Rules.ExactIdentity(spec):Rules.Identity(spec)),("$a",active?1:0),("$id",id)); tx.Commit();
        }
    }
    private static string? ExistingSubmission(SqliteConnection cn, SqliteTransaction tx, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new BusinessException("提交标识为空，无法记账。");
        return Scalar(cn, tx, "SELECT Id FROM Document WHERE SubmissionKey=$key", ("$key",key)) as string;
    }
    private static List<StockImpact> BuildImpacts(SqliteConnection cn, SqliteTransaction? tx, DocumentKind kind, IReadOnlyList<LineInput> lines)
    {
        if (kind is not (DocumentKind.Purchase or DocumentKind.Sale)) throw new BusinessException("业务类型无效。");
        if (lines.Count == 0) throw new BusinessException("请至少添加一种货品。");
        foreach (var l in lines) Rules.Quantity(l.Quantity,kind==DocumentKind.Purchase);
        var results = new List<StockImpact>();
        foreach (var group in lines.GroupBy(x => x.ProductId))
        {
            var quantity = group.Sum(x => x.Quantity); Rules.Quantity(quantity,kind==DocumentKind.Purchase);
            var product = ProductById(cn,tx,group.Key);
            if (!product.Active) throw new BusinessException($"{product.Display} 已停用，请先启用。");
            if (kind==DocumentKind.Sale && quantity>product.Warehouse) throw new BusinessException($"{product.Display}：需要{quantity}，仓库可用{product.Warehouse}。请先调整分布。");
            var impact = new StockImpact(product,kind==DocumentKind.Purchase?quantity:-quantity,0); Rules.Balance(impact.WarehouseAfter,impact.StoreAfter); results.Add(impact);
        }
        return results;
    }
    public IReadOnlyList<StockImpact> Preview(DocumentKind kind, IReadOnlyList<LineInput> lines)
    { lock(gate) { using var cn=Connect(); return BuildImpacts(cn,null,kind,lines); } }
    public string Commit(DocumentKind kind, string channel, IReadOnlyList<LineInput> lines, string submissionKey, IReadOnlyList<string>? photos=null,IReadOnlyDictionary<string,string>? photoMetadata=null)
    {
        if (kind==DocumentKind.Purchase && channel!="厂家" || kind==DocumentKind.Sale && channel is not ("零售" or "批发")) throw new BusinessException("请选择有效渠道。");
        lock(gate)
        {
            using var cn=Connect(); using var tx=cn.BeginTransaction();
            var existing=ExistingSubmission(cn,tx,submissionKey); if(existing!=null) { tx.Rollback(); RememberCommittedDocument(existing); return existing; }
            var impacts=BuildImpacts(cn,tx,kind,lines);
            var id=WriteDocument(cn,tx,kind,channel,impacts,submissionKey,inputs:lines);
            var copied=new List<string>();
            try
            {
                foreach(var path in (photos??[]).Distinct())
                {
                    var ext=Path.GetExtension(path).ToLowerInvariant(); if(ext is not (".jpg" or ".jpeg" or ".png")) throw new BusinessException("附件只支持JPG和PNG图片。");
                    var relative=$"photos/{Guid.NewGuid():N}{ext}"; var target=ManagedPath(relative);
                    File.Copy(path,target); copied.Add(target);
                    Run(cn,tx,"INSERT INTO Attachment(DocumentId,Path,Metadata) VALUES($id,$p,$m)",("$id",id),("$p",relative),("$m",photoMetadata?.GetValueOrDefault(path)??""));
                }
                FaultInjector?.Invoke("BeforeCommit"); tx.Commit(); RememberCommittedDocument(id); return id;
            }
            catch { foreach(var path in copied) { try { File.Delete(path); } catch { } } throw; }
        }
    }
    public StockImpact PreviewTransfer(long productId,long quantity,bool warehouseToStore)
    {
        Rules.Quantity(quantity); lock(gate) { using var cn=Connect(); var p=ProductById(cn,null,productId); return TransferImpact(p,quantity,warehouseToStore); }
    }
    private static StockImpact TransferImpact(Product p,long q,bool forward)
    {
        if(!p.Active) throw new BusinessException("货品已停用，请先启用。");
        var impact=new StockImpact(p,forward?-q:q,forward?q:-q); Rules.Balance(impact.WarehouseAfter,impact.StoreAfter); return impact;
    }
    public string Transfer(long productId,long quantity,bool warehouseToStore,string key)
    {
        Rules.Quantity(quantity); lock(gate)
        {
            using var cn=Connect(); using var tx=cn.BeginTransaction(); var old=ExistingSubmission(cn,tx,key); if(old!=null)return old;
            var impact=TransferImpact(ProductById(cn,tx,productId),quantity,warehouseToStore);
            var id=WriteDocument(cn,tx,DocumentKind.Transfer,warehouseToStore?"仓库→店面":"店面→仓库",[impact],key); tx.Commit(); return id;
        }
    }
    public IReadOnlyList<StockImpact> PreviewVoid(string id)
    { lock(gate) { using var cn=Connect(); return VoidImpacts(cn,null,GetDocument(cn,null,id)); } }
    private static List<StockImpact> VoidImpacts(SqliteConnection cn,SqliteTransaction? tx,DocumentRecord doc)
    {
        if(doc.Kind is not(DocumentKind.Purchase or DocumentKind.Sale) || doc.Status==RecordStatus.Voided) throw new BusinessException("该单据不能作废或已经作废。");
        var result=doc.Lines.GroupBy(l=>l.ProductId).Select(g=>new StockImpact(ProductById(cn,tx,g.Key),-g.Sum(l=>l.WarehouseDelta),-g.Sum(l=>l.StoreDelta))).ToList();
        foreach(var i in result) Rules.Balance(i.WarehouseAfter,i.StoreAfter); return result;
    }
    public string Void(string id,string reason,string key)
    {
        reason=reason.Trim(); if(reason.Length is 0 or >500) throw new BusinessException("请填写作废原因（1到500字）。");
        lock(gate)
        {
            using var cn=Connect(); using var tx=cn.BeginTransaction(); var old=ExistingSubmission(cn,tx,key); if(old!=null)return old;
            var doc=GetDocument(cn,tx,id); var impacts=VoidImpacts(cn,tx,doc);
            var reversal=WriteDocument(cn,tx,DocumentKind.Void,doc.Channel,impacts,key,id,reason,reverse:doc.Lines);
            Run(cn,tx,"UPDATE Document SET Status='Voided',VoidId=$v,VoidAt=$at,Reason=$r WHERE Id=$id",("$v",reversal),("$at",clock.Now.ToString("O")),("$r",reason),("$id",id));
            FaultInjector?.Invoke("BeforeCommit"); tx.Commit(); return reversal;
        }
    }
    private string WriteDocument(SqliteConnection cn,SqliteTransaction tx,DocumentKind kind,string channel,IReadOnlyList<StockImpact> impacts,string key,string? original=null,string reason="",IReadOnlyList<LineInput>? inputs=null,IReadOnlyList<DocumentLine>? reverse=null)
    {
        var id=Guid.NewGuid().ToString("N"); var now=clock.Now; var number=$"{now:yyyyMMdd-HHmmss}-{id[..8].ToUpperInvariant()}";
        var businessDate=DateOnly.FromDateTime(now.DateTime);
        var cutoff=DateOnly.Parse((string)Scalar(cn,tx,"SELECT Value FROM Metadata WHERE Key='Cutoff'")!);
        if(businessDate<cutoff)throw new BusinessException("本机日期早于已清理的记录范围，请修正系统日期后再记账。");
        Run(cn,tx,"INSERT INTO Document(Id,Number,Kind,Channel,Status,BusinessDate,OccurredAt,SubmissionKey,OriginalId,Reason) VALUES($id,$n,$k,$c,'Valid',$d,$at,$key,$o,$r)",
            ("$id",id),("$n",number),("$k",kind.ToString()),("$c",channel),("$d",Rules.DateText(businessDate)),("$at",now.ToString("O")),("$key",key),("$o",original),("$r",reason));
        var detail=reverse?.Select(l=>l with{WarehouseDelta=-l.WarehouseDelta,StoreDelta=-l.StoreDelta}).ToList()
            ?? inputs?.Select((l,index)=>{var p=impacts.Single(i=>i.Product.Id==l.ProductId).Product;return new DocumentLine(p.Id,p.Name,p.Spec,p.Unit,l.Quantity,kind==DocumentKind.Purchase?l.Quantity:-l.Quantity,0,index+1,l.PhotoOrder,l.OriginalOrder,p.Type,p.Color,p.MaterialCode,l.Marker,l.RawUnit,l.RawName,l.ReviewNote);}).ToList()
            ?? impacts.Select((i,index)=>new DocumentLine(i.Product.Id,i.Product.Name,i.Product.Spec,i.Product.Unit,kind==DocumentKind.Opening?i.WarehouseDelta+i.StoreDelta:Math.Abs(i.WarehouseDelta),i.WarehouseDelta,i.StoreDelta,index+1,Type:i.Product.Type,Color:i.Product.Color,MaterialCode:i.Product.MaterialCode)).ToList();
        foreach(var l in detail)
            Run(cn,tx,"""
                INSERT INTO DocumentLine(DocumentId,ProductId,Name,Spec,Unit,Quantity,WarehouseDelta,StoreDelta,LineOrder,PhotoOrder,OriginalOrder,Type,Color,MaterialCode,Marker,RawUnit,RawName,ReviewNote)
                VALUES($id,$p,$n,$s,$u,$q,$w,$t,$order,$photo,$original,$type,$color,$code,$marker,$rawunit,$rawname,$note)
                """,("$id",id),("$p",l.ProductId),("$n",l.Name),("$s",l.Spec),("$u",l.Unit),("$q",l.Quantity),("$w",l.WarehouseDelta),("$t",l.StoreDelta),
                ("$order",l.LineOrder),("$photo",l.PhotoOrder),("$original",l.OriginalOrder),("$type",l.Type.ToString()),("$color",l.Color),("$code",l.MaterialCode),("$marker",l.Marker),("$rawunit",l.RawUnit),("$rawname",l.RawName),("$note",l.ReviewNote));
        foreach(var i in impacts)
        {
            Rules.Balance(i.WarehouseAfter,i.StoreAfter);
            Run(cn,tx,"UPDATE StockBalance SET Warehouse=$w,Store=$s WHERE ProductId=$id",("$w",i.WarehouseAfter),("$s",i.StoreAfter),("$id",i.Product.Id));
            Run(cn,tx,"UPDATE Product SET LastUsed=$date WHERE Id=$id",("$date",Rules.DateText(clock.Today)),("$id",i.Product.Id));
            FaultInjector?.Invoke("AfterBalance");
        }
        return id;
    }
    internal string ManagedPath(string relative)
    {
        var full=Path.GetFullPath(Path.Combine(DataDirectory,relative));
        if(Path.IsPathRooted(relative)||!full.StartsWith(DataDirectory+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new BusinessException("数据文件路径越界。");
        return full;
    }
    public string AttachmentPath(string relative)
    {
        if(!relative.Replace('\\','/').StartsWith("photos/",StringComparison.Ordinal))throw new BusinessException("货单照片路径无效。");
        return ManagedPath(relative);
    }
    public DateOnly Cutoff { get { lock(gate) { using var cn=Connect(); return DateOnly.Parse((string)Scalar(cn,null,"SELECT Value FROM Metadata WHERE Key='Cutoff'")!); } } }
    public DocumentRecord GetDocument(string id) { lock(gate) { using var cn=Connect(); return GetDocument(cn,null,id); } }
    public Product GetProduct(long id) { lock(gate) {using var cn=Connect();return ProductById(cn,null,id);} }
    private static DocumentRecord GetDocument(SqliteConnection cn,SqliteTransaction? tx,string id)
    {
        using var cmd=Command(cn,tx,"SELECT Id,Number,Kind,Channel,Status,BusinessDate,OccurredAt,OriginalId,VoidId,VoidAt,Reason FROM Document WHERE Id=$id",("$id",id));
        DocumentRecord doc;
        using(var r=cmd.ExecuteReader())
        {
            if(!r.Read()) throw new BusinessException("单据不存在或已过保留期。");
            doc=new(r.GetString(0),r.GetString(1),Enum.Parse<DocumentKind>(r.GetString(2)),r.GetString(3),Enum.Parse<RecordStatus>(r.GetString(4)),DateOnly.Parse(r.GetString(5)),r.GetString(6),r.IsDBNull(7)?null:r.GetString(7),r.IsDBNull(8)?null:r.GetString(8),r.IsDBNull(9)?null:r.GetString(9),r.GetString(10),[],[]);
        }
        var lines=new List<DocumentLine>(); using(var c=Command(cn,tx,$"SELECT {LineColumns} FROM DocumentLine l WHERE DocumentId=$id ORDER BY LineOrder",("$id",id)))
        using(var r=c.ExecuteReader()) { while(r.Read()) lines.Add(ReadLine(r)); }
        var photos=new List<string>(); using(var c=Command(cn,tx,"SELECT Path FROM Attachment WHERE DocumentId=$id ORDER BY Id",("$id",id))) using(var r=c.ExecuteReader()) { while(r.Read()) photos.Add(r.GetString(0)); }
        return doc with{Lines=lines,Attachments=photos};
    }
    public IReadOnlyList<DocumentRecord> Query(QueryFilter filter)
    {
        var result=new List<DocumentRecord>(); int page=0;
        while(true){var batch=QueryPage(filter,page++,1000);result.AddRange(batch.Items);if(result.Count>=batch.Count)return result;}
    }
}

using Microsoft.Data.Sqlite;

namespace Stock.Core;

public sealed record LedgerRow(string Number,string OccurredAt,DocumentKind Kind,string Channel,RecordStatus Status,string VoidAt,string Reason,DocumentLine Line);
public sealed partial class StockService
{
    public bool HasPhotoHash(string hash)
    {using var cn=Connect();return Convert.ToInt64(Scalar(cn,null,"SELECT COUNT(*) FROM Attachment WHERE instr(Metadata,$hash)>0",("$hash",hash)))>0;}
    private const string LineColumns="l.ProductId,l.Name,l.Spec,l.Unit,l.Quantity,l.WarehouseDelta,l.StoreDelta,l.LineOrder,l.PhotoOrder,l.OriginalOrder,l.Type,l.Color,l.MaterialCode,l.Marker,l.RawUnit,l.RawName,l.ReviewNote";
    private static DocumentLine ReadLine(SqliteDataReader r)=>new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt64(4),r.GetInt64(5),r.GetInt64(6),r.GetInt32(7),r.GetInt32(8),r.GetString(9),Enum.Parse<ProductType>(r.GetString(10)),r.IsDBNull(11)?null:r.GetString(11),r.IsDBNull(12)?null:r.GetString(12),r.GetString(13),r.GetString(14),r.GetString(15),r.GetString(16));
    private static string IdentityWhere(string alias,ProductFilter? f,List<(string Key,object? Value)> args)
    {
        if(f==null)return "1=1";
        var conditions=new List<string>();
        void Many(string column,IEnumerable<string>? values)
        {
            if(values==null)return;var keys=new List<string>();foreach(var value in values){var key="$p"+args.Count;args.Add((key,value));keys.Add(key);}
            if(keys.Count>0)conditions.Add($"{alias}.{column} IN ({string.Join(',',keys)})");
        }
        Many("Type",f.Types?.Select(t=>t.ToString()));Many("Name",f.Names);Many("Spec",f.Specs);
        var before=conditions.Count;Many("Color",f.Colors);
        if(f.MissingColor){if(conditions.Count>before)conditions[^1]=$"({conditions[^1]} OR {alias}.Color IS NULL)";else conditions.Add($"{alias}.Color IS NULL");}
        if(f.Code.Length>0){args.Add(("$code",f.Code));conditions.Add($"instr(lower(COALESCE({alias}.MaterialCode,'')),lower($code))>0");}
        return conditions.Count==0?"1=1":string.Join(" AND ",conditions);
    }
    private static string SearchWhere(string alias,string search,List<(string Key,object? Value)> args)
    {args.Add(("$search",search));return $"(instr(lower({alias}.Name),lower($search))>0 OR instr(lower({alias}.Spec),lower($search))>0 OR instr(lower(COALESCE({alias}.MaterialCode,'')),lower($search))>0)";}
    private static (string Doc,string Line,List<(string Key,object? Value)> Args) QueryWhere(QueryFilter f)
    {
        if(f.Start>f.End)throw new BusinessException("开始日期不能晚于结束日期。");
        var args=new List<(string Key,object? Value)>{("$start",Rules.DateText(f.Start)),("$end",Rules.DateText(f.End))};
        var doc="d.BusinessDate>=$start AND d.BusinessDate<=$end";
        if(!f.Inventory)doc+=" AND d.Kind IN ('Purchase','Sale')";
        if(f.Kind!=null){doc+=" AND d.Kind=$kind";args.Add(("$kind",f.Kind.ToString()));}
        if(!string.IsNullOrEmpty(f.Channel)){doc+=" AND d.Channel=$channel";args.Add(("$channel",f.Channel));}
        if(f.Status!=StatusFilter.All){doc+=" AND d.Status=$status";args.Add(("$status",f.Status==StatusFilter.Valid?"Valid":"Voided"));}
        var line=IdentityWhere("l",f.Products,args)+" AND "+SearchWhere("l",f.Search,args);
        return(doc,line,args);
    }
    public PageResult<Product> ProductPage(ProductFilter? filter=null,string search="",bool includeInactive=false,int page=0,int size=100)
    {
        if(page<0||size is <1 or >1000)throw new BusinessException("分页范围无效。");
        lock(gate)
        {
            using var cn=Connect();var args=new List<(string Key,object? Value)>();var where=IdentityWhere("p",filter,args)+" AND "+SearchWhere("p",search,args)+(includeInactive?"":" AND p.Active=1");
            var count=Convert.ToInt64(Scalar(cn,null,$"SELECT COUNT(*) FROM Product p WHERE {where}",args.ToArray()));
            args.Add(("$limit",size));args.Add(("$offset",(long)page*size));
            using var cmd=Command(cn,null,$"SELECT {ProductColumns} FROM Product p JOIN StockBalance b ON b.ProductId=p.Id WHERE {where} ORDER BY p.Name,p.Spec,p.Id LIMIT $limit OFFSET $offset",args.ToArray());
            using var r=cmd.ExecuteReader();var items=new List<Product>();while(r.Read())items.Add(ReadProduct(r));return new(items,count);
        }
    }
    public PageResult<DocumentRecord> QueryPage(QueryFilter filter,int page=0,int size=100)
    {
        if(page<0||size is <1 or >1000)throw new BusinessException("分页范围无效。");
        lock(gate)
        {
            using var cn=Connect();var w=QueryWhere(filter);var where=w.Doc+$" AND EXISTS(SELECT 1 FROM DocumentLine l INDEXED BY UX_Line_Order WHERE l.DocumentId=d.Id AND {w.Line})";
            var count=Convert.ToInt64(Scalar(cn,null,$"SELECT COUNT(*) FROM Document d WHERE {where}",w.Args.ToArray()));
            w.Args.Add(("$limit",size));w.Args.Add(("$offset",(long)page*size));var ids=new List<string>();
            using(var cmd=Command(cn,null,$"SELECT d.Id FROM Document d WHERE {where} ORDER BY julianday(d.OccurredAt) DESC,d.Id DESC LIMIT $limit OFFSET $offset",w.Args.ToArray()))
            using(var r=cmd.ExecuteReader())while(r.Read())ids.Add(r.GetString(0));
            var docs=new List<DocumentRecord>();foreach(var id in ids)
            {
                var doc=GetDocument(cn,null,id);var lines=new List<DocumentLine>();var args=w.Args.ToList();args.Add(("$doc",id));
                using(var cmd=Command(cn,null,$"SELECT {LineColumns} FROM DocumentLine l INDEXED BY UX_Line_Order WHERE l.DocumentId=$doc AND {w.Line} ORDER BY l.LineOrder",args.ToArray()))
                using(var r=cmd.ExecuteReader())while(r.Read())lines.Add(ReadLine(r));docs.Add(doc with{Lines=lines});
            }
            return new(docs,count);
        }
    }
    public IReadOnlyList<string> FilterCandidates(string field,string search="",int page=0,ProductFilter? filter=null)
    {
        if(field is not("Name" or "Spec" or "Color" or "MaterialCode")||page<0)throw new BusinessException("筛选字段无效。");
        using var cn=Connect();var args=new List<(string Key,object? Value)>{("$text",search),("$offset",(long)page*100)};
        var where=IdentityWhere("p",filter,args);
        using var cmd=Command(cn,null,$"SELECT DISTINCT p.{field} FROM Product p WHERE {where} AND p.{field} IS NOT NULL AND instr(lower(p.{field}),lower($text))>0 ORDER BY p.{field} LIMIT 100 OFFSET $offset",args.ToArray());
        using var r=cmd.ExecuteReader();var result=new List<string>();while(r.Read())result.Add(r.GetString(0));return result;
    }
    public IEnumerable<LedgerRow> Ledger(QueryFilter filter,CancellationToken token=default)
    {
        using var cn=Connect();using var tx=cn.BeginTransaction(deferred:true);var w=QueryWhere(filter);
        using var cmd=Command(cn,tx,$"SELECT {LineColumns},d.Number,d.OccurredAt,d.Kind,d.Channel,d.Status,COALESCE(d.VoidAt,''),d.Reason FROM Document d JOIN DocumentLine l ON l.DocumentId=d.Id WHERE {w.Doc} AND {w.Line} ORDER BY julianday(d.OccurredAt) DESC,d.Id DESC,l.LineOrder",w.Args.ToArray());
        using var registration=token.Register(cmd.Cancel);using var r=cmd.ExecuteReader();
        while(r.Read()){token.ThrowIfCancellationRequested();yield return new(r.GetString(17),r.GetString(18),Enum.Parse<DocumentKind>(r.GetString(19)),r.GetString(20),Enum.Parse<RecordStatus>(r.GetString(21)),r.GetString(22),r.GetString(23),ReadLine(r));}
    }
    public IEnumerable<Product> Inventory(ProductFilter? filter=null,string search="",CancellationToken token=default)
    {
        using var cn=Connect();var args=new List<(string Key,object? Value)>();var where=IdentityWhere("p",filter,args)+" AND "+SearchWhere("p",search,args);
        using var cmd=Command(cn,null,$"SELECT {ProductColumns} FROM Product p JOIN StockBalance b ON b.ProductId=p.Id WHERE {where} ORDER BY p.Name,p.Spec,p.Id",args.ToArray());
        using var r=cmd.ExecuteReader();while(r.Read()){token.ThrowIfCancellationRequested();yield return ReadProduct(r);}
    }
    public IReadOnlyList<SummaryRow> Summarize(GroupFields fields,ProductFilter? products=null,string search="",QueryFilter? ledger=null,CancellationToken token=default)
    {
        using var cn=Connect();var args=new List<(string Key,object? Value)>();var alias=ledger==null?"p":"l";
        var name=fields.Name?$"{alias}.Name":"''";var spec=fields.Spec?$"{alias}.Spec":"''";var color=fields.Color?$"COALESCE({alias}.Color,'未记录')":"''";
        var group=$"{name},{spec},{color},{alias}.Type,{alias}.Unit";string sql;
        if(ledger==null)
        {var where=IdentityWhere("p",products,args)+" AND "+SearchWhere("p",search,args);sql=$"SELECT {group},SUM(b.Warehouse),SUM(b.Store),0,'','' FROM Product p JOIN StockBalance b ON b.ProductId=p.Id WHERE {where} GROUP BY {group}";}
        else
        {var w=QueryWhere(ledger);args=w.Args;sql=$"SELECT {group},0,0,SUM(l.Quantity),d.Kind,d.Channel FROM Document d JOIN DocumentLine l ON l.DocumentId=d.Id WHERE {w.Doc} AND {w.Line} AND d.Status='Valid' AND d.Kind IN ('Purchase','Sale') GROUP BY {group},d.Kind,d.Channel";}
        using var cmd=Command(cn,null,sql,args.ToArray());using var registration=token.Register(cmd.Cancel);using var r=cmd.ExecuteReader();var result=new List<SummaryRow>();
        while(r.Read()){token.ThrowIfCancellationRequested();result.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),Enum.Parse<ProductType>(r.GetString(3)),r.GetString(4),r.GetInt64(5),r.GetInt64(6),r.GetInt64(7),r.GetString(8),r.GetString(9)));}return result;
    }
}

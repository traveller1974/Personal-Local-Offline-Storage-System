using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace Stock.Core;

public sealed record ExportOptions(IReadOnlyList<string>? Columns=null,GroupFields? Group=null,bool SummaryOnly=false);
public static class ExcelExporter
{
    public static readonly string[] LedgerColumns=["单据编号","发生时间","业务类型","渠道","货品名称","规格","单位","数量","状态","作废时间","作废原因","类型","颜色","物料编码","标识","行序","照片序号","原单序号","原始单位","原始名称","核对说明"];
    public static readonly string[] InventoryColumns=["货品名称","规格","类型","颜色","物料编码","单位","仓库","店面","合计","资料状态"];
    private const string Ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private sealed record Table(string Name,IReadOnlyList<string> Headers,IEnumerable<object?[]> Rows);
    public static void Export(StockService service,QueryFilter filter,string destination,ExportOptions? options=null,CancellationToken token=default)
    {
        options??=new();var columns=Select(LedgerColumns,options.Columns);
        IEnumerable<object?[]> Details()
        {
            foreach(var r in service.Ledger(filter,token))
            {
                var l=r.Line;object?[] values=[r.Number,r.OccurredAt,Rules.KindName(r.Kind),r.Channel,l.Name,l.Spec,l.Unit,l.Quantity,r.Status==RecordStatus.Voided?"已作废":"有效",r.VoidAt,r.Reason,l.TypeText,l.Color??"未记录",l.MaterialCode??"未记录",l.Marker,l.LineOrder,l.PhotoOrder,l.OriginalOrder,l.RawUnit,l.RawName,l.ReviewNote];
                yield return columns.Select(c=>values[Array.IndexOf(LedgerColumns,c)]).ToArray();
            }
        }
        var fields=options.Group??new GroupFields();
        IEnumerable<object?[]> Summary()=>service.Summarize(fields,ledger:filter,token:token).Select(s=>new object?[]{s.Name,s.Spec,s.Unit,Rules.KindName(Enum.Parse<DocumentKind>(s.Kind)),s.Channel,s.Quantity,Rules.TypeName(s.Type),s.Color});
        var tables=new List<Table>();if(!options.SummaryOnly)tables.Add(new("明细",columns,Details()));
        tables.Add(new("汇总",["货品名称","规格","单位","业务类型","渠道","有效数量","类型","颜色"],Summary()));
        tables.Add(Notes(service,JsonSerializer.Serialize(filter),fields));Write(destination,tables,token);
    }
    public static void ExportInventory(StockService service,ProductFilter? filter,string search,string destination,ExportOptions? options=null,CancellationToken token=default)
    {
        options??=new();var columns=Select(InventoryColumns,options.Columns);var fields=options.Group??new GroupFields();
        IEnumerable<object?[]> Details()
        {
            foreach(var p in service.Inventory(filter,search,token))
            {object?[] values=[p.Name,p.Spec,Rules.TypeName(p.Type),p.Color??"未记录",p.MaterialCode??"未记录",p.Unit,p.Warehouse,p.Store,p.Total,p.Complete?"完整":"待补全"];yield return columns.Select(c=>values[Array.IndexOf(InventoryColumns,c)]).ToArray();}
        }
        IEnumerable<object?[]> Summary()=>service.Summarize(fields,filter,search,token:token).Select(s=>new object?[]{s.Name,s.Spec,Rules.TypeName(s.Type),s.Color,s.Unit,s.Warehouse,s.Store,s.Total});
        var tables=new List<Table>();if(!options.SummaryOnly)tables.Add(new("库存明细",columns,Details()));
        tables.Add(new("库存汇总",["货品名称","规格","类型","颜色","单位","仓库","店面","合计"],Summary()));
        tables.Add(Notes(service,JsonSerializer.Serialize(new{filter,search}),fields));Write(destination,tables,token);
    }
    private static IReadOnlyList<string> Select(string[] defaults,IReadOnlyList<string>? selected)
    {var cols=selected??defaults;if(cols.Count==0||cols.Any(c=>!defaults.Contains(c))||cols.Distinct().Count()!=cols.Count)throw new BusinessException("请选择有效且不重复的导出列。");return cols;}
    private static Table Notes(StockService service,string filter,GroupFields group)=>new("筛选说明",["条件","值"],new object?[][]
    { ["筛选条件",filter],["汇总维度",JsonSerializer.Serialize(group)],["生成时间",DateTimeOffset.Now.ToString("O")],["保留起始日期",Rules.DateText(service.Cutoff)],["汇总口径","类型和单位始终分开；进出货仅统计有效原单。"],["范围","全部筛选结果，包括当前页面以外的明细。"] });
    private static XmlWriter Writer(Stream stream)=>XmlWriter.Create(stream,new XmlWriterSettings{Encoding=new UTF8Encoding(false),CloseOutput=false});
    private static string Column(int number){var s="";while(number>0){number--;s=(char)('A'+number%26)+s;number/=26;}return s;}
    private static void Write(string destination,IReadOnlyList<Table> tables,CancellationToken token)
    {
        var full=Path.GetFullPath(destination);Directory.CreateDirectory(Path.GetDirectoryName(full)!);var pending=full+"."+Guid.NewGuid().ToString("N")+".tmp.xlsx";
        try
        {
            using(var zip=ZipFile.Open(pending,ZipArchiveMode.Create))
            {
                var names=new List<string>();
                foreach(var table in tables)
                {
                    using var rows=table.Rows.GetEnumerator();token.ThrowIfCancellationRequested();var more=rows.MoveNext();var first=true;var part=0;
                    while(more||first)
                    {
                        first=false;token.ThrowIfCancellationRequested();var name=table.Name+(part++==0?"":part.ToString());names.Add(name);
                        using var stream=zip.CreateEntry($"xl/worksheets/sheet{names.Count}.xml",CompressionLevel.Fastest).Open();using var w=Writer(stream);
                        w.WriteStartElement("worksheet",Ns);w.WriteStartElement("sheetViews",Ns);w.WriteStartElement("sheetView",Ns);w.WriteAttributeString("workbookViewId","0");w.WriteStartElement("pane",Ns);w.WriteAttributeString("ySplit","1");w.WriteAttributeString("topLeftCell","A2");w.WriteAttributeString("state","frozen");w.WriteEndElement();w.WriteEndElement();w.WriteEndElement();
                        w.WriteStartElement("sheetData",Ns);var row=1;WriteRow(w,row++,table.Headers.Cast<object?>().ToArray());
                        while(row<=1048576 && more){token.ThrowIfCancellationRequested();WriteRow(w,row++,rows.Current);more=rows.MoveNext();}
                        w.WriteEndElement();w.WriteStartElement("autoFilter",Ns);w.WriteAttributeString("ref",$"A1:{Column(table.Headers.Count)}{row-1}");w.WriteEndElement();w.WriteEndElement();
                    }
                }
                Entry(zip,"[Content_Types].xml",w=>
                {var n="http://schemas.openxmlformats.org/package/2006/content-types";w.WriteStartElement("Types",n);void Type(string tag,string attr,string path,string type){w.WriteStartElement(tag,n);w.WriteAttributeString(attr,path);w.WriteAttributeString("ContentType",type);w.WriteEndElement();}
                 Type("Default","Extension","rels","application/vnd.openxmlformats-package.relationships+xml");Type("Default","Extension","xml","application/xml");Type("Override","PartName","/xl/workbook.xml","application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");for(var i=1;i<=names.Count;i++)Type("Override","PartName",$"/xl/worksheets/sheet{i}.xml","application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");w.WriteEndElement();});
                Entry(zip,"_rels/.rels",w=>Relations(w,[("rId1","officeDocument","xl/workbook.xml")]));
                Entry(zip,"xl/_rels/workbook.xml.rels",w=>Relations(w,names.Select((_,i)=>($"rId{i+1}","worksheet",$"worksheets/sheet{i+1}.xml"))));
                Entry(zip,"xl/workbook.xml",w=>{w.WriteStartElement("workbook",Ns);w.WriteStartElement("sheets",Ns);for(var i=0;i<names.Count;i++){w.WriteStartElement("sheet",Ns);w.WriteAttributeString("name",names[i]);w.WriteAttributeString("sheetId",(i+1).ToString());w.WriteAttributeString("r","id","http://schemas.openxmlformats.org/officeDocument/2006/relationships",$"rId{i+1}");w.WriteEndElement();}w.WriteEndElement();w.WriteEndElement();});
            }
            token.ThrowIfCancellationRequested();File.Move(pending,full,true);
        }
        finally{if(File.Exists(pending))File.Delete(pending);}
    }
    private static void WriteRow(XmlWriter w,int row,IReadOnlyList<object?> values)
    {
        w.WriteStartElement("row",Ns);w.WriteAttributeString("r",row.ToString(CultureInfo.InvariantCulture));
        for(var i=0;i<values.Count;i++)
        {
            w.WriteStartElement("c",Ns);w.WriteAttributeString("r",Column(i+1)+row);var value=values[i];
            if(value is long or int){w.WriteElementString("v",Ns,Convert.ToString(value,CultureInfo.InvariantCulture));}
            else{w.WriteAttributeString("t","inlineStr");w.WriteStartElement("is",Ns);w.WriteStartElement("t",Ns);w.WriteAttributeString("xml","space","http://www.w3.org/XML/1998/namespace","preserve");var text=value?.ToString()??"";if(text.Length>32767)text=text[..32767];w.WriteString(new string(text.Where(XmlConvert.IsXmlChar).ToArray()));w.WriteEndElement();w.WriteEndElement();}
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }
    private static void Entry(ZipArchive zip,string path,Action<XmlWriter> write){using var stream=zip.CreateEntry(path).Open();using var w=Writer(stream);write(w);}
    private static void Relations(XmlWriter w,IEnumerable<(string Id,string Type,string Target)> items)
    {const string ns="http://schemas.openxmlformats.org/package/2006/relationships";w.WriteStartElement("Relationships",ns);foreach(var item in items){w.WriteStartElement("Relationship",ns);w.WriteAttributeString("Id",item.Id);w.WriteAttributeString("Type","http://schemas.openxmlformats.org/officeDocument/2006/relationships/"+item.Type);w.WriteAttributeString("Target",item.Target);w.WriteEndElement();}w.WriteEndElement();}
}

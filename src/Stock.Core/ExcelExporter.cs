using ClosedXML.Excel;

namespace Stock.Core;

public static class ExcelExporter
{
    public static void Export(StockService service,QueryFilter filter,string destination)
    {
        var docs=service.Query(filter); using var book=new XLWorkbook();
        var headers=new[]{"单据编号","发生时间","业务类型","渠道","货品名称","规格","单位","数量","状态","作废时间","作废原因"};
        var sheet=book.Worksheets.Add("明细"); Header(sheet,headers); var row=2; var page=1;
        foreach(var doc in docs) foreach(var line in doc.Lines)
        {
            if(!string.IsNullOrEmpty(filter.Search)&&!line.Name.Contains(filter.Search,StringComparison.OrdinalIgnoreCase)&&!line.Spec.Contains(filter.Search,StringComparison.OrdinalIgnoreCase))continue;
            if(row>1048576) { sheet=book.Worksheets.Add("明细"+(++page)); Header(sheet,headers); row=2; }
            var values=new[]{doc.Number,doc.OccurredAt,Rules.KindName(doc.Kind),doc.Channel,line.Name,line.Spec,line.Unit};
            for(var i=0;i<values.Length;i++) sheet.Cell(row,i+1).Value=values[i];
            sheet.Cell(row,8).Value=line.Quantity; sheet.Cell(row,9).Value=doc.Status==RecordStatus.Voided?"已作废":"有效";
            sheet.Cell(row,10).Value=doc.VoidAt??""; sheet.Cell(row,11).Value=doc.Reason; row++;
        }
        foreach(var s in book.Worksheets) Format(s);
        var summary=book.Worksheets.Add("汇总"); Header(summary,["货品名称","规格","单位","业务类型","渠道","有效数量"]); row=2;
        var groups=docs.Where(d=>d.Status==RecordStatus.Valid&&d.Kind is DocumentKind.Purchase or DocumentKind.Sale)
            .SelectMany(d=>d.Lines.Select(l=>(Doc:d,Line:l)))
            .Where(x=>string.IsNullOrEmpty(filter.Search)||x.Line.Name.Contains(filter.Search,StringComparison.OrdinalIgnoreCase)||x.Line.Spec.Contains(filter.Search,StringComparison.OrdinalIgnoreCase))
            .GroupBy(x=>(x.Line.ProductId,x.Line.Name,x.Line.Spec,x.Line.Unit,x.Doc.Kind,x.Doc.Channel));
        foreach(var group in groups)
        {
            var k=group.Key; var values=new[]{k.Name,k.Spec,k.Unit,Rules.KindName(k.Kind),k.Channel};
            for(var i=0;i<values.Length;i++)summary.Cell(row,i+1).Value=values[i];
            summary.Cell(row++,6).Value=group.Sum(x=>x.Line.Quantity);
        }
        Format(summary);
        var note=book.Worksheets.Add("筛选说明"); Header(note,["条件","值"]);
        var count=docs.Sum(d=>d.Lines.Count(l=>string.IsNullOrEmpty(filter.Search)||l.Name.Contains(filter.Search,StringComparison.OrdinalIgnoreCase)||l.Spec.Contains(filter.Search,StringComparison.OrdinalIgnoreCase)));
        var notes=new[]{("开始日期",Rules.DateText(filter.Start)),("结束日期",Rules.DateText(filter.End)),("类型",filter.Kind==null?"全部":Rules.KindName(filter.Kind.Value)),("渠道",filter.Channel??"全部"),
            ("状态",filter.Status switch{StatusFilter.All=>"全部",StatusFilter.Voided=>"已作废",_=>"有效"}),("货品搜索",filter.Search),("生成时间",DateTimeOffset.Now.ToString("O")),
            ("保留起始日期",Rules.DateText(service.Cutoff)),("明细行数",count.ToString()),("汇总口径","只汇总当前有效的进出货原单；作废反向流水不计入销售或进货。")};
        row=2; foreach(var (key,value) in notes) { note.Cell(row,1).Value=key; note.Cell(row++,2).Value=value; } Format(note); note.Column(2).Width=75;
        var full=Path.GetFullPath(destination); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp=full+"."+Guid.NewGuid().ToString("N")+".tmp.xlsx";
        try { book.SaveAs(temp); File.Move(temp,full,true); } finally { if(File.Exists(temp))File.Delete(temp); }
    }
    private static void Header(IXLWorksheet sheet,IReadOnlyList<string> values)
    { for(var i=0;i<values.Count;i++)sheet.Cell(1,i+1).Value=values[i]; }
    private static void Format(IXLWorksheet sheet)
    {
        var range=sheet.RangeUsed(); if(range==null)return;
        sheet.Row(1).Style.Fill.BackgroundColor=XLColor.FromHtml("#16394B"); sheet.Row(1).Style.Font.FontColor=XLColor.White; sheet.Row(1).Style.Font.Bold=true;
        range.SetAutoFilter(); sheet.SheetView.FreezeRows(1); sheet.Columns().Width=22; sheet.Row(1).Height=26;
    }
}

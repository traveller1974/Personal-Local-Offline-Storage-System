using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Stock.Core;

namespace Stock.Desktop;

public static class QueryDialogs
{
    public static Product? PickProduct(Window owner,StockService service)
    {
        var window=Ui.Dialog(owner,"选择已有货品",950,700);var root=new DockPanel{Margin=new(20)};var header=new WrapPanel();DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);var text=new TextBox{Width=250};header.Children.Add(text);ProductFilter? filter=null;var page=0;long count=0;Product? saved=null;
        var grid=Ui.Table(Array.Empty<Product>(),("类型","TypeText",75),("名称","Name",150),("规格","Spec",220),("颜色","ColorText",100),("编码","MaterialCode",120),("单位","Unit",55));var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);root.Children.Add(grid);
        void Load(){var batch=service.ProductPage(filter,text.Text,page:page);count=batch.Count;grid.ItemsSource=batch.Items;}
        header.Children.Add(Ui.Button("搜索",()=>{page=0;Load();}));header.Children.Add(Ui.Button("组合筛选",()=>{if(Filter(window,service,filter,out var f)){filter=f;page=0;Load();}}));
        footer.Children.Add(Ui.Row(Ui.Button("上一页",()=>{page=Math.Max(0,page-1);Load();}),Ui.Button("下一页",()=>{if((page+1)*100<count)page++;Load();}),Ui.Button("选择此货品",()=>{if(grid.SelectedItem is not Product p)throw new BusinessException("请选择货品。");saved=p;window.DialogResult=true;},true)));window.Content=root;Load();window.ShowDialog();return saved;
    }
    public static bool Filter(Window owner,StockService service,ProductFilter? current,out ProductFilter? result)
    {
        var window=Ui.Dialog(owner,"选择要查找的货品条件",1000,760);var root=new DockPanel{Margin=new(22)};ProductFilter? saved=current;
        var bottom=new StackPanel();DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);
        bottom.Children.Add(Ui.Text("同一栏勾选多个值时，匹配其中一个即可；不同栏的条件需要同时满足。"));
        var types=new Dictionary<ProductType,CheckBox>();var typePanel=new WrapPanel();
        foreach(var type in Enum.GetValues<ProductType>()){var box=new CheckBox{Content=Rules.TypeName(type),IsChecked=current?.Types?.Contains(type)==true,Margin=new(0,0,16,10)};types[type]=box;typePanel.Children.Add(box);}
        bottom.Children.Add(typePanel);var code=new TextBox{Text=current?.Code??""};Ui.Field(bottom,"物料编码搜索",code);
        var missing=new CheckBox{Content="同时查找旧货品中没有记录颜色的货品",IsChecked=current?.MissingColor==true};bottom.Children.Add(missing);
        var sets=new[]{new HashSet<string>(current?.Names??[]),new HashSet<string>(current?.Specs??[]),new HashSet<string>(current?.Colors??[])};
        var fields=new[]{"Name","Spec","Color"};var titles=new[]{"名称","规格","颜色（空值为无颜色）"};var grid=new Grid();for(var i=0;i<3;i++)grid.ColumnDefinitions.Add(new());root.Children.Add(grid);
        for(var index=0;index<3;index++)
        {
            var field=fields[index];var set=sets[index];var panel=new DockPanel{Margin=new(8)};Grid.SetColumn(panel,index);grid.Children.Add(panel);
            var header=new StackPanel();DockPanel.SetDock(header,Dock.Top);panel.Children.Add(header);header.Children.Add(Ui.Text(titles[index],true));
            var search=new TextBox();header.Children.Add(search);var list=new StackPanel();var page=0;var label=Ui.Text("");
            void Load(){list.Children.Clear();foreach(var value in service.FilterCandidates(field,search.Text,page)){var key=value;var box=new CheckBox{Content=new TextBlock{Text=key.Length==0?"无颜色":key,TextWrapping=TextWrapping.Wrap},IsChecked=set.Contains(key),Margin=new(0,5,0,5)};box.Checked+=(_,_)=>set.Add(key);box.Unchecked+=(_,_)=>set.Remove(key);list.Children.Add(box);}label.Text=$"第{page+1}页，已选{set.Count}项";}
            header.Children.Add(Ui.Button("搜索候选",()=>{page=0;Load();}));var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);panel.Children.Add(footer);footer.Children.Add(label);footer.Children.Add(Ui.Row(Ui.Button("上一页",()=>{page=Math.Max(0,page-1);Load();}),Ui.Button("下一页",()=>{page++;Load();})));panel.Children.Add(new ScrollViewer{Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});Load();
        }
        bottom.Children.Add(Ui.Row(Ui.Button("取消",()=>window.DialogResult=false),Ui.Button("清除筛选",()=>{saved=null;window.DialogResult=true;}),Ui.Button("应用筛选",()=>{saved=new(types.Where(t=>t.Value.IsChecked==true).Select(t=>t.Key).ToList(),sets[0].ToList(),sets[1].ToList(),sets[2].ToList(),code.Text,missing.IsChecked==true);window.DialogResult=true;},true)));
        window.Content=root;var ok=window.ShowDialog()==true;result=saved;return ok;
    }
    public static void Summary(Window owner,StockService service,ProductFilter? filter,string search,QueryFilter? ledger=null)
    {
        var window=Ui.Dialog(owner,ledger is null?"库存分类汇总":"有效进出货汇总",1100,720);var root=new DockPanel{Margin=new(22)};var header=new StackPanel();DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
        var name=new CheckBox{Content="名称",IsChecked=true};var spec=new CheckBox{Content="规格",IsChecked=true};var color=new CheckBox{Content="颜色",IsChecked=true};header.Children.Add(Ui.Text("勾选分类方式，查看每类合计。不同货品类型分别统计，双击一行查看明细。",true));header.Children.Add(Ui.Row(name,spec,color));
        var grid=ledger is null?Ui.Table(Array.Empty<SummaryRow>(),("名称","Name",160),("规格","Spec",200),("颜色","Color",100),("类型","TypeText",80),("单位","Unit",65),("仓库","Warehouse",90),("店面","Store",90),("合计","Total",90)):
            Ui.Table(Array.Empty<SummaryRow>(),("名称","Name",160),("规格","Spec",200),("颜色","Color",100),("类型","TypeText",80),("单位","Unit",65),("业务","KindText",80),("渠道","Channel",80),("有效数量","Quantity",110));root.Children.Add(grid);
        async Task Load(){var fields=new GroupFields(name.IsChecked==true,spec.IsChecked==true,color.IsChecked==true);var rows=await Task.Run(()=>service.Summarize(fields,filter,search,ledger));grid.ItemsSource=rows;}
        header.Children.Add(Ui.Button("更新汇总",async()=>await Ui.TryAsync(Load),true));
        grid.MouseDoubleClick+=(_,_)=>Ui.Try(()=>
        {
            if(grid.SelectedItem is not SummaryRow row)return;
            var selection=new ProductFilter([row.Type],name.IsChecked==true?[row.Name]:filter?.Names,spec.IsChecked==true?[row.Spec]:filter?.Specs,color.IsChecked==true&&row.Color!="未记录"?[row.Color]:filter?.Colors,filter?.Code??"",color.IsChecked==true&&row.Color=="未记录");
            DetailPage(window,service,selection,search,ledger is null?null:ledger with{Products=selection});
        });
        window.Content=root;window.Loaded+=async(_,_)=>await Ui.TryAsync(Load);window.ShowDialog();
    }
    private static void DetailPage(Window owner,StockService service,ProductFilter filter,string search,QueryFilter? ledger)
    {
        var window=Ui.Dialog(owner,"查看这一类的明细",1000,680);var root=new DockPanel{Margin=new(20)};var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var grid=ledger is null?Ui.Table(Array.Empty<Product>(),("名称","Name",150),("规格","Spec",200),("颜色","ColorText",100),("类型","TypeText",80),("物料编码","MaterialCode",120),("单位","Unit",60),("仓库","Warehouse",85),("店面","Store",85),("合计","Total",85)):
            Ui.Table(Array.Empty<RecordRow>(),("日期","Date",110),("业务","Kind",70),("渠道","Channel",80),("匹配货品与数量","Items",0),("状态","Status",85));root.Children.Add(grid);var page=0;long count=0;var label=Ui.Text("");
        void Load(){if(ledger==null){var batch=service.ProductPage(filter,search,true,page);count=batch.Count;grid.ItemsSource=batch.Items;}else{var batch=service.QueryPage(ledger,page);count=batch.Count;grid.ItemsSource=batch.Items.Select(d=>new RecordRow(d)).ToList();}label.Text=$"第{page+1}页，共{count}项";}
        footer.Children.Add(label);footer.Children.Add(Ui.Row(Ui.Button("上一页",()=>{page=Math.Max(0,page-1);Load();}),Ui.Button("下一页",()=>{if((page+1)*100<count)page++;Load();})));
        grid.MouseDoubleClick+=(_,_)=>Ui.Try(()=>{if(grid.SelectedItem is RecordRow row)StockDialogs.Document(window,service,row.Id);});window.Content=root;Load();window.ShowDialog();
    }
    public static void Export(Window owner,StockService service,bool inventory,ProductFilter? filter,string search,QueryFilter? ledger=null)
    {
        var window=Ui.Dialog(owner,"导出全部筛选结果",700,760);var root=new DockPanel{Margin=new(22)};var panel=new StackPanel();var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);root.Children.Add(new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        panel.Children.Add(Ui.Text("勾选需要导出的信息。",true));var columns=(inventory?ExcelExporter.InventoryColumns:ExcelExporter.LedgerColumns).Select(c=>new CheckBox{Content=c,IsChecked=true,Margin=new(0,5,0,5)}).ToList();foreach(var box in columns)panel.Children.Add(box);
        var name=new CheckBox{Content="按名称汇总",IsChecked=true};var spec=new CheckBox{Content="按规格汇总",IsChecked=true};var color=new CheckBox{Content="按颜色汇总",IsChecked=true};var only=new CheckBox{Content="仅导出汇总"};footer.Children.Add(Ui.Row(name,spec,color));footer.Children.Add(only);var status=Ui.Text("");footer.Children.Add(status);CancellationTokenSource? cancellation=null;var busy=false;
        var export=new Button{Content="选择文件并导出"};var cancel=new Button{Content="取消导出"};cancel.Click+=(_,_)=>cancellation?.Cancel();footer.Children.Add(Ui.Row(export,cancel));window.Closing+=(_,e)=>{if(busy){cancellation?.Cancel();e.Cancel=true;status.Text="正在取消，临时文件不会替换原文件。";}};
        export.Click+=async(_,_)=>
        {
            if(busy)return;var dialog=new SaveFileDialog{Filter="Excel (*.xlsx)|*.xlsx",DefaultExt=".xlsx",FileName=$"{(inventory?"库存":"进出货")}_{DateTime.Now:yyyyMMdd}.xlsx"};if(dialog.ShowDialog(window)!=true)return;
            var options=new ExportOptions(columns.Where(c=>c.IsChecked==true).Select(c=>(string)c.Content).ToList(),new(name.IsChecked==true,spec.IsChecked==true,color.IsChecked==true),only.IsChecked==true);busy=true;export.IsEnabled=panel.IsEnabled=false;cancellation=new();status.Text="正在导出全部结果，可取消。";
            try{await Task.Run(()=>{if(inventory)ExcelExporter.ExportInventory(service,filter,search,dialog.FileName,options,cancellation.Token);else ExcelExporter.Export(service,ledger!,dialog.FileName,options,cancellation.Token);});status.Text="导出完成。";}
            catch(OperationCanceledException){status.Text="已取消，原文件保持不变。";}catch(Exception ex){status.Text=ex.Message;}
            finally{busy=false;export.IsEnabled=panel.IsEnabled=true;cancellation.Dispose();cancellation=null;}
        };
        window.Content=root;window.ShowDialog();
    }
}

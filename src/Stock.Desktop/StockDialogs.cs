using System.Windows;
using System.Windows.Controls;
using Stock.Core;
using Stock.Desktop.Controls;

namespace Stock.Desktop;

public static class StockDialogs
{
    public static Product? Product(Window owner,StockService service,Product? original=null,bool zeroOnly=false,string name="",string spec="",ProductType type=ProductType.Accessory,string color="",string code="")
    {
        var w=Ui.Dialog(owner,original is null?"新增完整身份货品":"货品资料与状态",660,850);Product? saved=null;var dock=new DockPanel{Margin=new(26)};
        var p=new StackPanel();var nameBox=new TextBox{Text=original?.Name??name,MaxLength=120};var specBox=new TextBox{Text=original?.Spec??spec,MaxLength=500};var unit=new TextBox{Text=original?.Unit??"件",IsReadOnly=true,MaxLength=20};
        var choices=new[]{ProductType.Unknown,ProductType.Vehicle,ProductType.Battery,ProductType.Charger,ProductType.Accessory}.Select(t=>new Choice<ProductType>(Rules.TypeName(t),t)).ToList();
        var typeBox=new ComboBox{ItemsSource=choices,SelectedItem=choices.Single(t=>t.Value==(original?.Type??type))};
        var colorBox=new TextBox{Text=original?.Color??color,MaxLength=120};var codeBox=new TextBox{Text=original?.MaterialCode??code,MaxLength=120};
        typeBox.SelectionChanged+=(_,_)=>unit.Text=typeBox.SelectedItem is Choice<ProductType> selected&&selected.Value!=ProductType.Unknown?Rules.Unit(selected.Value):original?.Unit??"待确认";
        unit.Text=(original?.Type??type)==ProductType.Unknown?original?.Unit??"待确认":Rules.Unit(original?.Type??type);
        Ui.Field(p,"货物类型",typeBox);Ui.Field(p,"货品名称（必填）",nameBox);Ui.Field(p,"完整规格 / 型号",specBox);Ui.Field(p,"颜色（电池、充电器留空）",colorBox);Ui.Field(p,"物料编码（无编码须人工确认留空）",codeBox);Ui.Field(p,"系统单位",unit);
        var correction=new CheckBox{Content="明确补全 / 纠正此货品身份，不拆分或合并现有库存；历史快照不改写",IsChecked=false,Margin=new(0,12,0,12)};
        if(original is not null)p.Children.Add(correction);
        var warehouse=new QuantityBox{Minimum=0,Value="0"};var store=new QuantityBox{Minimum=0,Value="0"};var active=new CheckBox{Content="启用此货品",IsChecked=original?.Active??true};
        if(original is null&&!zeroOnly) { Ui.Field(p,"仓库期初数量",warehouse);Ui.Field(p,"店面期初数量",store); }
        if(original is not null)p.Children.Add(active);
        p.Children.Add(Ui.Text(original is not null?"历史单据保留原名称和规格。停用后无法进出货，库存与历史仍保留。":zeroOnly?"新货品初始库存为0；照片中的数量将在最终确认进货后入库。":"期初库存单独记账，不计入厂家进货。"));
        var key=Guid.NewGuid().ToString("N");var actions=Ui.Row(Ui.Button("取消",()=>w.DialogResult=false),Ui.Button(original is null?"预览并确认":"保存货品",()=>
        {
            var selectedType=((Choice<ProductType>)typeBox.SelectedItem).Value;
            if(original is not null)
            {
                var changed=original.Name!=nameBox.Text||original.Spec!=specBox.Text||original.Type!=selectedType||(original.Color??"")!=colorBox.Text||(original.MaterialCode??"")!=codeBox.Text;
                if(changed&&correction.IsChecked!=true)throw new BusinessException("修改身份须明确勾选资料补全 / 纠错；不会自动拆分或合并库存。");
                if(correction.IsChecked==true)service.CompleteProduct(original.Id,selectedType,nameBox.Text,specBox.Text,colorBox.Text,codeBox.Text,active.IsChecked==true);
                else service.UpdateProduct(original.Id,original.Name,original.Spec,active.IsChecked==true);
                saved=service.GetProduct(original.Id);w.DialogResult=true;return;
            }
            var wh=zeroOnly?0:warehouse.Number;var st=zeroOnly?0:store.Number;Rules.Balance(wh,st);
            if(string.IsNullOrWhiteSpace(nameBox.Text)||string.IsNullOrWhiteSpace(unit.Text))throw new BusinessException("请填写货品名称和单位。");
            var proposed=new Product(0,nameBox.Text,specBox.Text,unit.Text,true,0,0);
            var productName=nameBox.Text;var productSpec=specBox.Text;var productUnit=unit.Text;
            var productColor=colorBox.Text;var productCode=codeBox.Text;
            if(Ui.Confirm(w,"确认货品与期初库存",$"类型：{Rules.TypeName(selectedType)}；名称：{productName}；规格：{productSpec}；颜色：{productColor}；编码：{productCode}；单位：{productUnit}\n仓库期初：{wh}；店面期初：{st}；合计：{wh+st}",[new(proposed,wh,st)],()=>Task.Run(()=>service.CreateProduct(selectedType,productName,productSpec,productColor,productCode,wh,st,key).ToString()),out var id))
            {saved=service.GetProduct(long.Parse(id!));w.DialogResult=true;}
        },true));
        actions.HorizontalAlignment=HorizontalAlignment.Right;DockPanel.SetDock(actions,Dock.Bottom);dock.Children.Add(actions);dock.Children.Add(new ScrollViewer{Content=p,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});w.Content=dock;w.ShowDialog();return saved;
    }
    public static string? Transfer(Window owner,StockService service,Product product)
    {
        string? saved=null;var w=Ui.Dialog(owner,"调整库存分布",690,530);var p=new StackPanel{Margin=new(26)};
        p.Children.Add(Ui.Text(product.Display,true));var direction=new ComboBox{ItemsSource=new[]{"仓库→店面","店面→仓库"},SelectedIndex=0};var quantity=new QuantityBox();Ui.Field(p,"调拨方向",direction);Ui.Field(p,"调拨数量",quantity);var impactText=Ui.Text("");p.Children.Add(impactText);
        void Update()
        {
            try { var i=service.PreviewTransfer(product.Id,quantity.Number,direction.SelectedIndex==0);impactText.Text=$"仓库：{i.Product.Warehouse} → {i.WarehouseAfter}\n店面：{i.Product.Store} → {i.StoreAfter}\n合计：{i.Product.Total} → {i.TotalAfter}（保持不变）"; }
            catch(Exception ex) { impactText.Text=ex is BusinessException?ex.Message:"无法预览，请重试。"; }
        }
        quantity.ValueChanged+=(_,_)=>Update();direction.SelectionChanged+=(_,_)=>Update();Update();var key=Guid.NewGuid().ToString("N");
        p.Children.Add(Ui.Row(Ui.Button("取消",()=>w.DialogResult=false),Ui.Button("确认调整",()=>
        {
            var q=quantity.Number;var forward=direction.SelectedIndex==0;var preview=service.PreviewTransfer(product.Id,q,forward);
            if(Ui.Confirm(w,"确认调整",$"{product.Display} · {direction.SelectedItem}",[preview],()=>Task.Run(()=>service.Transfer(product.Id,q,forward,key)),out var id)) {saved=id;w.DialogResult=true;}
        },true)));w.Content=p;w.ShowDialog();return saved;
    }
    public static string? Document(Window owner,StockService service,string id)
    {
        var doc=service.GetDocument(id);string? changed=null;var w=Ui.Dialog(owner,"单据详情",1000,710);var dock=new DockPanel{Margin=new(24)};
        var head=new StackPanel();head.Children.Add(Ui.Text($"{Rules.KindName(doc.Kind)} · {doc.Channel} · {(doc.Status==RecordStatus.Valid?"有效":"已作废")}",true));
        head.Children.Add(Ui.Text($"编号：{doc.Number}\n业务日期：{doc.BusinessDate:yyyy-MM-dd}　发生时间：{DateTimeOffset.Parse(doc.OccurredAt):yyyy-MM-dd HH:mm:ss zzz}"));
        if(doc.VoidAt is not null)head.Children.Add(Ui.Text($"作废时间：{doc.VoidAt}\n原因：{doc.Reason}"));
        if(doc.OriginalId is not null) { string origin;try{origin=service.GetDocument(doc.OriginalId).Number;}catch(BusinessException){origin="原单已过保留期";} head.Children.Add(Ui.Text($"关联原单：{origin}\n作废原因：{doc.Reason}")); }
        DockPanel.SetDock(head,Dock.Top);dock.Children.Add(head);var foot=new StackPanel{Margin=new(0,16,0,0)};
        if(doc.Attachments.Count>0){var photos=new WrapPanel();for(var i=0;i<doc.Attachments.Count;i++){var path=service.AttachmentPath(doc.Attachments[i]);photos.Children.Add(Ui.Button($"查看照片 {i+1}",()=>Ui.Photo(w,path)));}foot.Children.Add(photos);}
        var actions=Ui.Row(Ui.Button("关闭",()=>w.Close()));
        if(doc.Status==RecordStatus.Valid&&doc.Kind is DocumentKind.Purchase or DocumentKind.Sale)actions.Children.Add(Ui.Button("作废此单据",()=>
        {
            var impacts=service.PreviewVoid(id);var reasonWindow=Ui.Dialog(w,"填写作废原因",600,390);var panel=new StackPanel{Margin=new(24)};panel.Children.Add(Ui.Text("作废会在当前日期生成反向库存流水。请填写原因并核对库存变化。"));var reason=new TextBox{AcceptsReturn=true,Height=100,MaxLength=500};panel.Children.Add(reason);
            var key=Guid.NewGuid().ToString("N");panel.Children.Add(Ui.Row(Ui.Button("取消",()=>reasonWindow.Close()),Ui.Button("预览作废",()=>
            { if(string.IsNullOrWhiteSpace(reason.Text))throw new BusinessException("请填写作废原因。");var reasonText=reason.Text;if(Ui.Confirm(reasonWindow,"确认作废",$"原单：{doc.Number}\n原因：{reasonText}",impacts,()=>Task.Run(()=>service.Void(id,reasonText,key)),out var reversal)){changed=reversal;reasonWindow.DialogResult=true;w.Close();} },true)));reasonWindow.Content=panel;reasonWindow.ShowDialog();
        }));
        foot.Children.Add(actions);DockPanel.SetDock(foot,Dock.Bottom);dock.Children.Add(foot);dock.Children.Add(Ui.Table(doc.Lines,("行序","LineOrder",55),("照片序号","PhotoOrder",70),("原序号","OriginalOrder",65),("货品名称","Name",160),("规格","Spec",220),("颜色","ColorText",100),("实发数量","Quantity",85),("单位","Unit",55),("标识","Marker",65),("类型","TypeText",75),("物料编码","CodeText",110),("原单位","RawUnit",65),("仓库变动","WarehouseDelta",100),("店面变动","StoreDelta",100),("核对说明","ReviewNote",200)));w.Content=dock;w.ShowDialog();return changed;
    }
}

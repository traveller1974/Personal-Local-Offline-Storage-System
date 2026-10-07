using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Stock.Core;

namespace Stock.Desktop.Controls;

public partial class QuantityBox : UserControl
{
    public static readonly DependencyProperty ValueProperty=DependencyProperty.Register(nameof(Value),typeof(string),typeof(QuantityBox),new FrameworkPropertyMetadata("1",FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,(d,_)=>((QuantityBox)d).Synchronize()));
    public static readonly DependencyProperty MinimumProperty=DependencyProperty.Register(nameof(Minimum),typeof(long),typeof(QuantityBox),new PropertyMetadata(1L,(d,_)=>((QuantityBox)d).Synchronize()));
    public string Value { get=>(string)GetValue(ValueProperty); set=>SetValue(ValueProperty,value); }
    public long Minimum { get=>(long)GetValue(MinimumProperty); set=>SetValue(MinimumProperty,value); }
    public bool IsValid => IntegerInput.TryParse(Value,Minimum,out _);
    public long Number => IntegerInput.TryParse(Value,Minimum,out var value)?value:throw new BusinessException("请输入范围内的整数数量。");
    public event EventHandler? ValueChanged;
    public QuantityBox() { InitializeComponent(); Synchronize(); }
    private void Synchronize()
    {
        if(Input is null)return;
        if(Input.Text!=Value)Input.Text=Value??"";
        var valid=IntegerInput.TryParse(Value,Minimum,out var q);
        Minus.IsEnabled=valid&&q>Minimum; Plus.IsEnabled=valid&&q<Rules.MaxQuantity;
        if(valid||string.IsNullOrEmpty(Value))Input.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
        else Input.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty,"DangerInk");
    }
    private void InputChanged(object sender,TextChangedEventArgs e) { SetCurrentValue(ValueProperty,Input.Text);Synchronize();ValueChanged?.Invoke(this,EventArgs.Empty); }
    private void Typed(object sender,TextCompositionEventArgs e) => e.Handled=e.Text.Any(c=>c is <'0' or >'9');
    private void Pasted(object sender,DataObjectPastingEventArgs e)
    { if(!e.DataObject.GetDataPresent(DataFormats.UnicodeText)||e.DataObject.GetData(DataFormats.UnicodeText) is not string text||text.Any(c=>c is <'0' or >'9'))e.CancelCommand(); }
    private void Decrease(object sender,RoutedEventArgs e) { if(IsValid&&Number>Minimum)SetCurrentValue(ValueProperty,(Number-1).ToString()); }
    private void Increase(object sender,RoutedEventArgs e) { if(IsValid&&Number<Rules.MaxQuantity)SetCurrentValue(ValueProperty,(Number+1).ToString()); }
}

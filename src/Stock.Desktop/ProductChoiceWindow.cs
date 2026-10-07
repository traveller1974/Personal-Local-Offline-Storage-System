using System.Windows;
using System.Windows.Controls;
using Stock.Core;

namespace Stock.Desktop;

internal sealed class ProductChoiceWindow : Window
{
    internal ProductChoiceWindow(Window owner, OcrReviewRow row, Product selected)
    {
        Style=(Style)Application.Current.FindResource(typeof(Window));
        Owner = owner; Title = "确认使用已有货品的资料"; Width = 650; Height = 620;
        MinWidth = 550; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new(24) };
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 16, 0, 0) };
        actions.Children.Add(Ui.Button("返回修改", () => DialogResult = false));
        actions.Children.Add(Ui.Button("使用这个货品的资料", () => DialogResult = true, true));
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var content = new StackPanel();
        content.Children.Add(Ui.Text("你选的货品与当前填写内容有差异", true));
        content.Children.Add(Ui.Text("确认后，以下资料会填入当前行。请对照照片检查；“已核对”可用来标记检查过的行。"));
        foreach (var difference in row.IdentityDifferences(selected))
            content.Children.Add(Ui.Text($"{difference.Label}\n当前填写：{Show(difference.Current)}\n选中货品：{Show(difference.Selected)}"));
        content.Children.Add(Ui.Text($"本次进货数量仍为：{row.Quantity}。照片识别原文会保留，已有货品档案不会被修改。"));
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }
    private static string Show(string text) => text.Length == 0 ? "未填写" : text;
}

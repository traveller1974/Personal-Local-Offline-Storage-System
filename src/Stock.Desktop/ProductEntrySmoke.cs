using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Stock.Core;
using Stock.Desktop.Controls;

namespace Stock.Desktop;

/// <summary>Exercises actual entry controls and modal creation using an isolated database.</summary>
internal static class ProductEntrySmoke
{
    internal static async Task RunAsync(Window owner, string output, Action<bool, string> check, Action<Window, string> capture)
    {
        Directory.CreateDirectory(output);
        var originalCheck=check;
        check=(condition,label)=>{File.AppendAllText(Path.Combine(output,"progress.txt"),label+Environment.NewLine);originalCheck(condition,label);};
        var previousError=Ui.AutomatedTestError;
        Ui.AutomatedTestError=ex=>throw new InvalidOperationException("Entry action failed during isolated test",ex);
        var stock = new StockService(Path.Combine(output, "run-" + Guid.NewGuid().ToString("N"), "Data"));
        stock.CreateProduct(ProductType.Vehicle, "飞驰一代", "48V", "薄荷绿", "", 0, 0, Key());
        stock.CreateProduct(ProductType.Vehicle, "飞驰二代", "60V", "天蓝", "", 0, 0, Key());
        var review = new OcrWindow(owner, new DraftViewModel(stock, DocumentKind.Purchase), Path.Combine(output, "review"))
            { Left = -4000, Top = -4000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        DraftWindow? purchase = null;
        try
        {
            var source = new RecognizedRow("1", "飞驰一代", "飞驰一代", "", "48V", "薄荷绿", ProductType.Vehicle, "成车", "1", "PC", "", []);
            review.ApplyCloudResult(new RecognitionResult([source], new Dictionary<ProductType, long?>(), [], true));
            review.Show(); review.Activate(); review.UpdateLayout();
            var fields = Children<ProductTermBox>(review.ReviewRows).ToDictionary(c => c.TermField);
            check(fields.Count == 3 && fields.Values.All(c => c.Service == stock), "Only name, spec and color in the real review card use the current local term source");
            var name = fields["Name"]; var spec = fields["Spec"]; var color = fields["Color"]; var row = review.Rows.Single();
            row.Reviewed = true;
            name.Editor.Focus(); name.Editor.Text = "飞驰"; await name.RefreshSuggestionsAsync();
            check(name.Suggestions.Items.Cast<string>().SequenceEqual(new[] { "飞驰一代", "飞驰二代" }) && row.Name == "飞驰", "Typing a keyword lists both existing names without replacing the typed text");
            check(!row.Reviewed && !row.CanReview, "Changing a suggested identity field still revokes review and requires exact product association");
            KeyPress(name, System.Windows.Input.Key.Enter);
            check(row.Name == "飞驰", "Enter without a selected suggestion preserves a new name");
            CapturePopup(name, Path.Combine(output, "name-suggestions.png"));
            KeyPress(name, System.Windows.Input.Key.Down); KeyPress(name, System.Windows.Input.Key.Enter);
            check(row.Name == "飞驰一代" && name.GetBindingExpression(ProductTermBox.TextProperty) is not null && !name.SuggestionsPopup.IsOpen,
                "Down and Enter select an existing term while retaining the caller's two-way binding");
            check(row.Spec == "48V" && row.Color == "薄荷绿" && !row.Reviewed, "Selecting a name changes that field only and does not mark the row reviewed");

            color.Editor.Focus(); color.Editor.Text = ""; await color.RefreshSuggestionsAsync();
            check(color.Suggestions.Items.Cast<string>().ToHashSet().SetEquals(new[] { "薄荷绿", "天蓝" }) && row.Color == "", "Empty color displays existing colors while preserving the empty value");
            CapturePopup(color, Path.Combine(output, "empty-color-suggestions.png"));
            color.Suggestions.UpdateLayout();
            var item = (ListBoxItem)color.Suggestions.ItemContainerGenerator.ContainerFromItem("天蓝");
            item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
            check(row.Color == "天蓝" && row.Name == "飞驰一代" && row.Spec == "48V", "Mouse selection fills the color only");

            spec.Editor.Focus(); spec.Editor.Text = "V"; await spec.RefreshSuggestionsAsync();
            check(spec.Suggestions.Items.Cast<string>().ToHashSet().SetEquals(new[] { "48V", "60V" }), "Spec supports substring keyword search independently of the other fields");
            spec.Suggestions.SelectedItem = "60V"; KeyPress(spec, System.Windows.Input.Key.Enter);
            name.Editor.Focus(); name.Editor.Text = "飞驰二代"; await name.RefreshSuggestionsAsync();
            KeyPress(name, System.Windows.Input.Key.Down); KeyPress(name, System.Windows.Input.Key.Enter);
            for(var attempt=0;attempt<200&&(row.Matching||row.Product?.Name!="飞驰二代");attempt++)await Task.Delay(10);
            check(row.Product?.Name == "飞驰二代" && row.CanReview && !row.Reviewed, $"Selecting three complete matching values uses the existing exact association and manual review gate ({row.Name}, {row.Spec}, {row.Color}, product={row.Product?.Name}, review={row.Reviewed})");

            name.Editor.Text = "飞驰";
            var older = name.RefreshSuggestionsAsync(true);
            name.Editor.Text = "独立新词条";
            var newer = name.RefreshSuggestionsAsync(true);
            await Task.WhenAll(older, newer);
            check(name.Suggestions.Items.Count == 0 && row.Name == "独立新词条", "A later keyword cancels stale results without overwriting free text");
            name.Editor.Text = "飞驰"; await name.RefreshSuggestionsAsync();
            KeyPress(name, System.Windows.Input.Key.Escape);
            check(!name.SuggestionsPopup.IsOpen && row.Name == "飞驰", "Escape closes suggestions and preserves the keyword as a possible new name");
            await name.RefreshSuggestionsAsync();
            check(!KeyPress(name, System.Windows.Input.Key.Tab).Handled && !name.SuggestionsPopup.IsOpen && row.Name == "飞驰", "Tab retains typed input and allows normal focus navigation");
            color.Editor.Focus(); color.Editor.Text = ""; await color.RefreshSuggestionsAsync();
            review.ApplyCreatedProduct(row,stock.Products().Single(p=>p.Name=="飞驰二代"));
            check(row.Name=="飞驰二代"&&row.Spec=="60V"&&row.Color=="天蓝"&&row.CanReview&&!row.Reviewed&&row.RawName=="飞驰一代"&&row.Quantity=="1",
                "A product confirmed in the creation form synchronizes editable identity and preserves original OCR evidence and quantity");
            color.CloseSuggestions(); name.Editor.Focus(); name.Editor.Text = "新的未保存名称";
            review.Close(); await Task.Delay(180);
            check(!name.SuggestionsPopup.IsOpen && stock.Products().Count == 2 && stock.Products().All(p => p.Total == 0), "Closing pending entry searches creates no product, reopens no popup and changes no stock");

            purchase = new DraftWindow(owner, stock, DocumentKind.Purchase)
                { Left = -4000, Top = -4000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
            purchase.Show(); purchase.ProductSearch.Text = "飞驰";
            check(purchase.NewProductButton.Visibility == Visibility.Visible, "Manual purchase exposes creation without leaving the draft");
            Exception? modalError = null; var stage = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                try
                {
                    var dialog = Application.Current.Windows.Cast<Window>().FirstOrDefault(w => w.Title == (stage == 0 ? "新建货品" : "确认货品与期初库存"));
                    if (dialog is null) return;
                    dialog.Left = -4000; dialog.Top = -4000; dialog.ShowInTaskbar = false; dialog.UpdateLayout();
                    if (stage == 0)
                    {
                        var inputs = Children<ProductTermBox>(dialog).ToDictionary(c => c.TermField);
                        check(inputs.Count == 3 && inputs["Name"].Text == "飞驰" && inputs["Color"].Text == "", "Actual new-product form suggests exactly the three requested fields and accepts the draft keyword");
                        inputs["Name"].Editor.Text = "飞驰"; inputs["Spec"].Editor.Text = "72V 新规格"; inputs["Color"].Editor.Text = "";
                        var types = Children<ComboBox>(dialog).Single();
                        types.SelectedItem = types.Items.Cast<Choice<ProductType>>().Single(c => c.Value == ProductType.Vehicle);
                        check(stock.Products().Count == 2, "Free-text entry does not create a product before explicit creation confirmation");
                        capture(dialog, Path.Combine(output, "new-product-fields.png"));
                        stage = 1;
                        // Rearm before entering another modal loop; a Tick is otherwise rearmed only after it returns.
                        timer.Stop(); timer.Start();
                        Children<Button>(dialog).Single(b => Equals(b.Content, "预览并确认")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    else
                    {
                        timer.Stop();
                        Children<Button>(dialog).Single(b => Equals(b.Content, "确认货品与期初库存")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                }
                catch (Exception ex)
                {
                    modalError = ex; timer.Stop();
                    foreach (var dialog in Application.Current.Windows.Cast<Window>().Where(w => w.Title is "新建货品" or "确认货品与期初库存").ToArray().Reverse()) dialog.Close();
                }
            };
            timer.Start();
            try { purchase.NewProductButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
            finally { timer.Stop(); }
            if (modalError is not null) throw modalError;
            var created = purchase.ProductPicker.SelectedItem as Product;
            check(created is { Name: "飞驰", Spec: "72V 新规格", Color: "", Total: 0 } && stock.Products().Count == 3,
                "Creating a new typed name keeps existing names and selects a separate zero-stock product in the purchase draft");
            check(purchase.ViewModel.Lines.Count == 0 && stock.Products().All(p => p.Total == 0), "New product creation alone does not add a purchase line or inventory");
            purchase.AddQuantity.Value = "3";
            Children<Button>(purchase).Single(b => Equals(b.Content, "添加到清单")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            purchase.ViewModel.Preview();
            check(purchase.ViewModel.Lines.Single().Product.Id == created!.Id && stock.Products().All(p => p.Total == 0), "Adding the new product and previewing still leave inventory unchanged");
            await purchase.ViewModel.CommitAsync();
            check(stock.GetProduct(created!.Id).Warehouse == 3 && stock.GetProduct(created.Id).Store == 0, "Final purchase confirmation alone adds the entered quantity to the new product");
            stock.ValidateIntegrity();
            var sale = new DraftWindow(owner, stock, DocumentKind.Sale);
            check(sale.NewProductButton.Visibility == Visibility.Collapsed, "The new purchase action does not alter the sale workflow");
            sale.Close();
        }
        finally { review.Close(); purchase?.Close(); Ui.AutomatedTestError=previousError; }
    }

    private static string Key() => Guid.NewGuid().ToString("N");
    private static KeyEventArgs KeyPress(ProductTermBox field, System.Windows.Input.Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(field.Editor), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        field.Editor.RaiseEvent(args); return args;
    }
    private static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var nested in Children<T>(child)) yield return nested;
        }
    }
    private static void CapturePopup(ProductTermBox field, string path)
    {
        field.PopupBorder.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(field.PopupBorder.ActualWidth), (int)Math.Ceiling(field.PopupBorder.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(field.PopupBorder);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}

using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OpenCvSharp;
using Stock.Core;
using Stock.Desktop.Controls;
using Window = System.Windows.Window;

namespace Stock.Desktop;

internal static class ReviewInteractionSmoke
{
    internal static async Task RunStandaloneAsync(string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool passed, string message) { if (!passed) throw new Exception(message); checks.Add(message); }
        var owner = new MainWindow(new MainViewModel(new StockService(Path.Combine(output, "owner", "Data"))))
            { Left = -4000, Top = -4000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        Application.Current.MainWindow = owner; owner.Show();
        try
        {
            await RunAsync(owner, output, Check, DesktopSmoke.Capture);
            File.WriteAllText(Path.Combine(output, "review-results.json"), JsonSerializer.Serialize(new { success = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "review-results.json"), JsonSerializer.Serialize(new { success = false, checks, error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = 1;
        }
        finally { owner.Close(); }
    }

    internal static async Task RunAsync(Window owner, string output, Action<bool, string> check, Action<Window, string> capture)
    {
        Directory.CreateDirectory(output);
        var stock = new StockService(Path.Combine(output, "r-" + Guid.NewGuid().ToString("N")[..8], "Data"));
        var first = stock.GetProduct(stock.CreateProduct(ProductType.Vehicle, "飞驰一代", "48V", "薄荷绿", "A1", 0, 0, Key()));
        var second = stock.GetProduct(stock.CreateProduct(ProductType.Vehicle, "飞驰二代", "60V", "天蓝", "B2", 0, 0, Key()));
        var source = new RecognizedRow("1", "飞驰一代（A1）", first.Name, first.MaterialCode!, first.Spec, first.Color!, first.Type, "成车", "2", "PC", "A", []);
        RecognitionResult Result(RecognizedRow row, long? total = null) => new([row], total.HasValue ? new Dictionary<ProductType, long?> { [ProductType.Vehicle] = total } : new Dictionary<ProductType, long?>(), [], true);
        var image = Path.Combine(output, "review-photo.png");
        using (var pixels = new Mat(420, 600, MatType.CV_8UC3, Scalar.White)) { Cv2.PutText(pixels, "REVIEW TEST", new(25, 75), HersheyFonts.HersheySimplex, 1, Scalar.Black, 2); Cv2.ImWrite(image, pixels); }
        var draft = new DraftWindow(owner, stock, DocumentKind.Purchase)
            { Left = -4000, Top = -4000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        draft.Show();
        var window = new OcrWindow(draft, draft.ViewModel, Path.Combine(output, "review"))
            { Left = -4000, Top = -4000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        var oldError = Ui.AutomatedTestError;
        Ui.AutomatedTestError = ex => throw new InvalidOperationException("Review UI action failed", ex);
        try
        {
            draft.Show(); window.Load(image); window.ApplyCloudResult(Result(source)); window.Show(); window.Activate(); window.UpdateLayout();
            var row = window.Rows.Single(); var reviewed = Named<CheckBox>(window, "ReviewedCheck"); var quantity = Named<QuantityBox>(window, "ActualQuantity");
            var input = (TextBox)quantity.FindName("Input");
            check(reviewed.IsEnabled && row.CanReview && !row.Reviewed, "Valid unreviewed row has an enabled review checkbox");
            Click(reviewed);
            check(row.Reviewed && reviewed.IsChecked == true && window.ResultSummaryText.Text.Contains("已核对1行"), "Checkbox click path marks the row reviewed and updates progress");
            Space(reviewed);
            check(!row.Reviewed && reviewed.IsChecked == false, "Space key cancels review through the actual checkbox key handlers");
            Space(reviewed);
            check(row.Reviewed && reviewed.IsChecked == true, "Space key marks the row reviewed through the actual checkbox key handlers");
            Click((Button)quantity.FindName("Plus")); Click((Button)quantity.FindName("Plus")); Click((Button)quantity.FindName("Minus"));
            check(row.Quantity == "3" && input.Text == "3" && quantity.GetBindingExpression(QuantityBox.ValueProperty) is not null, "Repeated plus and minus retain the binding and change the real row quantity");
            check(row.Reviewed && reviewed.IsChecked == true && row.CanReview, "Quantity buttons preserve the review marker and valid manual edits");
            input.Focus(); input.SelectAll(); input.Text = "8";
            check(row.Quantity == "8" && quantity.Value == "8" && row.RawQuantity == "2", "Typing a quantity after button clicks updates the row and retains original recognition");
            row.Quantity = "6";
            check(input.Text == "6", "Source updates still reach the input after repeated quantity clicks");
            foreach (var invalid in new[] { "", "-1", "1.5", "2147483648" })
            {
                input.Text = invalid; row.Reviewed=false; Click(reviewed);
                check(!row.Reviewed && reviewed.IsChecked == false && reviewed.IsEnabled && window.StatusText.Text.Contains("数量") && input.IsKeyboardFocused,
                    "Invalid quantity gives an actionable message and focuses the input: " + invalid);
            }
            input.Text = "0";
            check(!((Button)quantity.FindName("Minus")).IsEnabled && row.CanReview, "Zero quantity is valid for purchases and disables minus");
            input.Text = Rules.MaxQuantity.ToString();
            check(!((Button)quantity.FindName("Plus")).IsEnabled, "Maximum quantity disables plus");
            input.Text = "2";
            var fields = Children<ProductTermBox>(window.ReviewRows).ToDictionary(c => c.TermField);
            fields["Name"].Editor.Text = "新的货品"; await WaitMatch(row);
            Click(reviewed);
            check(!row.Reviewed && window.StatusText.Text.Contains("选择已有货品") && Named<Button>(window, "SearchExistingProduct").IsKeyboardFocused,
                "Unmatched row stays clickable and directs the user to choose or create a product");
            fields["Name"].Editor.Text = second.Name; fields["Spec"].Editor.Text = second.Spec; fields["Color"].Editor.Text = second.Color!;
            Named<TextBox>(window, "ProductCode").Text = second.MaterialCode!; await WaitMatch(row);
            check(row.Product?.Id == second.Id && row.CanReview && !row.Reviewed, "Manual typing matches a complete existing product without choosing suggestion entries");
            fields["Name"].Editor.Text = "早到但已过期"; fields["Name"].Editor.Text = second.Name; await WaitMatch(row);
            check(row.Product?.Id == second.Id && row.Name == second.Name, "A cancelled earlier match cannot overwrite later input");

            window.ApplyCloudResult(Result(source)); window.UpdateLayout(); row = window.Rows.Single();
            Named<TextBox>(Named<QuantityBox>(window, "ActualQuantity"), "Input").Text = "7";
            ScheduleProductChoice(false, capture, Path.Combine(output, "product-differences-cancel.png"));
            check(!window.ChooseExistingProduct(row, second) && row.Product?.Id == first.Id && row.Name == first.Name && row.Quantity == "7",
                "Cancelling the difference dialog retains current product, identity and quantity");
            ScheduleProductChoice(true, capture, Path.Combine(output, "product-differences-confirm.png"));
            check(window.ChooseExistingProduct(row, second) && row.Product?.Id == second.Id && row.Name == second.Name && row.Spec == second.Spec && row.Color == second.Color && row.MaterialCode == second.MaterialCode,
                "Confirmed product selection fills all identity fields through the shared choice handler");
            check(row.Quantity == "7" && row.RawName == source.RawName && row.RawQuantity == "2" && row.SourceSpec==source.Spec && row.SourceColor==source.Color && row.SourceCode==source.MaterialCode && !row.Reviewed,
                "Confirmed product selection preserves user quantity and original evidence and still needs manual review");
            row.RefreshProducts(stock.Products(), second); window.UpdateLayout();
            var productPicker = Named<ComboBox>(window, "ExistingProduct");
            ScheduleProductChoice(false, capture, Path.Combine(output, "dropdown-cancel.png"));
            productPicker.SetCurrentValue(ComboBox.SelectedItemProperty, first);
            check(row.Product?.Id == second.Id && ((Product?)productPicker.SelectedItem)?.Id == second.Id && row.Name == second.Name,
                "Cancelling a dropdown selection restores both selected product and displayed selection");
            ScheduleProductChoice(true, capture, Path.Combine(output, "dropdown-confirm.png"));
            productPicker.SetCurrentValue(ComboBox.SelectedItemProperty, first);
            check(row.Product?.Id == first.Id && row.Name == first.Name && row.Quantity == "7", "Dropdown selection uses the same confirmed product data flow");

            window.ApplyCloudResult(Result(source with { RawUnit = "", RawQuantity = "3" }, 3)); window.UpdateLayout(); row = window.Rows.Single();
            reviewed = Named<CheckBox>(window, "ReviewedCheck"); Click(reviewed);
            var confirmUnit = Named<CheckBox>(window, "ConfirmUnit");
            check(row.Reviewed && row.UnitConfirmationRequired && confirmUnit.IsVisible && row.Warning.Contains("单位"),
                "Missing invoice unit is visible but cannot veto manual review");
            Click(confirmUnit);
            check(row.UnitConfirmed && row.CanReview && row.Reviewed && row.RawUnit == "" && row.SourceRawUnit == "" && row.UnitReviewNote.Contains("人工确认"),
                "Explicit human unit confirmation unlocks review without silently changing the original code or quantity");
            Named<ComboBox>(window, "ProductTypePicker").SetCurrentValue(ComboBox.SelectedValueProperty, ProductType.Battery);
            check(!row.UnitConfirmed && !row.CanReview && row.Warning.Contains("清空颜色"), "Changing product type validates the product and explains forbidden battery color");
            window.ApplyCloudResult(Result(source with { RawUnit = "PAA", RawQuantity = "3" }, 3)); window.UpdateLayout(); row = window.Rows.Single();
            Click(Named<CheckBox>(window, "ConfirmUnit")); Click(Named<CheckBox>(window, "ReviewedCheck"));
            check(row.Reviewed && row.RawUnit == "PAA" && row.Quantity == "3", "Unexpected unit can be confirmed while preserving original code and entered quantity");
            ((TextBox)Named<QuantityBox>(window, "ActualQuantity").FindName("Input")).Text = "4"; Click(Named<CheckBox>(window, "ReviewedCheck"));
            window.ValidateCurrentForm();check(draft.ViewModel.Lines.Count==0,"A mismatched photo total is advisory and does not write stock");
            ((TextBox)Named<QuantityBox>(window, "ActualQuantity").FindName("Input")).Text = "3"; row.Reviewed=true;

            foreach (var size in new[] { (1360d, 900d), (1040d, 680d) })
                foreach (var scale in new[] { 1d, 1.25, 1.5 })
                {
                    window.Width = size.Item1 * scale; window.Height = size.Item2 * scale; window.Root.LayoutTransform = new ScaleTransform(scale, scale); window.UpdateLayout();
                    var qty = Named<QuantityBox>(window, "ActualQuantity"); qty.BringIntoView(); window.UpdateLayout();
                    check(qty.ActualWidth >= 170 && Children<Button>(qty).All(b => b.ActualWidth >= 38) && ((TextBox)qty.FindName("Input")).ActualWidth >= 85,
                        $"Quantity controls have usable width at {size.Item1}x{size.Item2}, scale {scale}");
                    var button = Named<CheckBox>(window, "ReviewedCheck"); button.BringIntoView(); window.UpdateLayout();
                    var offset = button.TranslatePoint(new System.Windows.Point(), window.ReviewScroller);
                    check(offset.Y >= -1 && offset.Y + button.ActualHeight <= window.ReviewScroller.ActualHeight + 1,
                        $"Review checkbox is reachable by scrolling at {size.Item1}x{size.Item2}, scale {scale}");
                }
            window.Root.LayoutTransform = Transform.Identity; window.Width = 1360; window.Height = 900; window.UpdateLayout();
            window.TotalCorrectionPanel.IsExpanded=false;
            var quantityCard=VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(Named<QuantityBox>(window,"ActualQuantity"))) as FrameworkElement;
            quantityCard!.BringIntoView(); window.UpdateLayout(); capture(window, Path.Combine(output, "quantity-review.png"));
            Named<CheckBox>(window, "ReviewedCheck").BringIntoView(); window.UpdateLayout(); capture(window, Path.Combine(output, "review-status.png"));
            var metadataBefore = stock.Products();
            window.Hide(); _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => Click(window.AddRowsButton))); window.ShowDialog();
            check(draft.ViewModel.Lines.Count == 1 && draft.ViewModel.Lines[0].Quantity == "3" && stock.Products().SequenceEqual(metadataBefore),
                "Actual add-to-purchase action imports the reviewed quantity without changing stock");
            using (var metadata = JsonDocument.Parse(draft.ViewModel.PhotoMetadata.Values.Single()))
            {
                var saved = metadata.RootElement.GetProperty("reviewed")[0];
                check(saved.GetProperty("SourceRawUnit").GetString() == "PAA" && saved.GetProperty("UnitConfirmed").GetBoolean() && saved.GetProperty("Quantity").GetString() == "3",
                    "Photo metadata retains the source unit and human confirmation alongside the quantity");
            }
            draft.Activate(); draft.UpdateLayout(); var imported = draft.ViewModel.Lines.Single();
            var draftQty = Children<QuantityBox>(draft.DraftGrid).Single(); Click((Button)draftQty.FindName("Plus"));
            check(imported.Quantity == "4" && imported.Reviewed && draftQty.GetBindingExpression(QuantityBox.ValueProperty) is not null,
                "Draft quantity edit keeps binding and preserves the optional review marker");
            var draftReview = Named<CheckBox>(draft, "DraftReviewedCheck"); Click(draftReview);
            check(!imported.Reviewed && draftReview.IsChecked == false, "Draft review marker remains optional and clickable");
            draft.ViewModel.Preview();check(stock.Products().All(p=>p.Total==0),"Valid edited quantity previews despite total mismatch and unchecked marker");
            draft.ViewModel.TotalCorrection = "逐行核实，照片合计漏记1辆，应为4辆。";
            draft.ViewModel.Preview(); capture(draft, Path.Combine(output, "draft-reviewed.png"));
            draft.Hide();
            _ = draft.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                ScheduleConfirmation(); Click(draft.PreviewButton);
            }));
            draft.ShowDialog();
            var document = stock.GetDocument(draft.SavedId!);
            check(document.Lines.Single().Quantity == 4 && stock.GetProduct(first.Id).Warehouse == 4 && stock.GetProduct(second.Id).Total == 0,
                "Actual preview and confirmation buttons commit exactly the final edited quantity to the chosen product");
            check(document.Lines.Single().ReviewNote.Contains("原单识别单位：PAA") && document.Lines.Single().ReviewNote.Contains("合计漏记"),
                "Saved document retains unit confirmation and separate total correction explanations");
            stock.ValidateIntegrity();
            check(stock.CorrectionMemory().Single().Final.Quantity==4&&stock.CorrectionMemory().Single().Original.RawQuantity=="3",
                "Correction memory reads the final committed draft quantity and keeps original extraction evidence");
        }
        finally { window.Close(); draft.Close(); Ui.AutomatedTestError = oldError; }
    }

    private static async Task WaitMatch(OcrReviewRow row)
    {
        for (var attempt = 0; attempt < 200 && row.Matching; attempt++) await Task.Delay(10);
        if (row.Matching) throw new Exception("Exact matching did not finish");
    }
    private static void ScheduleProductChoice(bool accept, Action<Window, string> capture, string file)
    {
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var dialog = Application.Current.Windows.OfType<ProductChoiceWindow>().Single(); dialog.UpdateLayout(); capture(dialog, file);
            Click(Children<Button>(dialog).Single(b => Equals(b.Content, accept ? "使用这个货品的资料" : "返回修改")));
        }));
    }
    private static void ScheduleConfirmation()
    {
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var dialog = Application.Current.Windows.Cast<Window>().Single(w => w.Title == "确认入库");
            Click(Children<Button>(dialog).Single(b => Equals(b.Content, "确认入库")));
        }));
    }
    // Invoke WPF's real click implementation: includes toggle, binding update and routed Click.
    private static void Click(ButtonBase control) => typeof(ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, null);
    private static void Space(CheckBox control)
    {
        control.Focus();
        control.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(control), 0, System.Windows.Input.Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
        control.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(control), 0, System.Windows.Input.Key.Space) { RoutedEvent = Keyboard.KeyUpEvent });
    }
    private static string Key() => Guid.NewGuid().ToString("N");
    private static T Named<T>(DependencyObject parent, string name) where T : FrameworkElement => Children<T>(parent).Single(c => c.Name == name);
    private static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T typed) yield return typed;
            foreach (var descendant in Children<T>(child)) yield return descendant;
        }
    }
}

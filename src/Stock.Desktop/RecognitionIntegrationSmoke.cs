using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp;
using Stock.Core;
using Stock.Desktop.Controls;
using Recognition = Stock.Recognition;
using Window = System.Windows.Window;

namespace Stock.Desktop;

/// <summary>Isolated offline integration checks. This entry point bypasses real-data startup and saved settings.</summary>
internal static class RecognitionIntegrationSmoke
{
    internal static async Task RunAsync(string output, bool expectedEmbedded)
    {
        Directory.CreateDirectory(output);
        var run = Path.Combine(output, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        var checks = new List<string>();
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks.Add(message); }
        var initialSwitch = QwenSettings.UsesEmbeddedRecognition;
        MainWindow? owner = null;
        OcrWindow? window = null;
        try
        {
            var configuration = new QwenConfiguration("offline-integration-secret", QwenConfiguration.Beijing);
            Check(initialSwitch == expectedEmbedded, "Published runtime configuration selects the expected default backend: " + expectedEmbedded);
            Check(QwenSettings.CreateService(configuration).GetType() == (expectedEmbedded ? typeof(EmbeddedRecognitionService) : typeof(QwenRecognitionService)),
                "Default service factory matches the published backend without reading saved settings");
            AppContext.SetSwitch(QwenSettings.EmbeddedRecognitionSwitch, false);
            Check(QwenSettings.CreateService(configuration) is QwenRecognitionService, "Original recognition provider remains included and usable");
            AppContext.SetSwitch(QwenSettings.EmbeddedRecognitionSwitch, true);
            Check(QwenSettings.CreateService(configuration) is EmbeddedRecognitionService, "Enabled upgrade factory selects the embedded EXE adapter");
            AppContext.SetSwitch(QwenSettings.EmbeddedRecognitionSwitch, initialSwitch);

            var cache = Path.Combine(run, "isolated-worker-cache");
            var executable = EmbeddedRecognitionPayload.Extract(cache);
            Check(Hash(executable) == EmbeddedRecognitionPayload.Sha256, "Extracted EXE matches the exact embedded payload SHA-256");
            Check(EmbeddedRecognitionPayload.Extract(cache) == executable, "Valid module cache is reused by content version");
            File.WriteAllText(executable, "corrupted local cache");
            Check(Hash(EmbeddedRecognitionPayload.Extract(cache)) == EmbeddedRecognitionPayload.Sha256, "Corrupted module cache is repaired from the bundled EXE");
            Check(!Directory.EnumerateFiles(cache, "*.tmp", SearchOption.AllDirectories).Any(), "Atomic extraction leaves no temporary payload file");

            var image = Path.Combine(run, "offline-confirmed.png");
            using (var pixels = new Mat(240, 400, MatType.CV_8UC3, Scalar.White))
            {
                Cv2.Rectangle(pixels, new OpenCvSharp.Rect(20, 50, 360, 150), new Scalar(180, 230, 180), -1);
                Cv2.PutText(pixels, "OFFLINE PIPE TEST", new(30, 35), HersheyFonts.HersheySimplex, .7, Scalar.Black, 1);
                Cv2.ImWrite(image, pixels);
            }
            var spec = "VQS-RC20/0800/12DGK0 · 完整规格、尺寸和标点\n48V / 20Ah / 多行内容保留";
            object Row(string order, string name, string code, string type, string evidence, string quantity, string unit, string marker) => new
            {
                originalOrder = order, rawName = name + "（" + code + "）", name, materialCode = code, spec, color = "", type,
                sectionEvidence = evidence, rawQuantity = quantity, rawUnit = unit, marker, issues = new[] { "离线验证条目，请人工核对" }
            };
            var model = JsonSerializer.Serialize(new
            {
                actualQuantityColumn = true,
                rows = new[] { Row("1", "测试成车", "123", "Vehicle", "成车分区原文", "2", "PC", "A1"),
                    Row("2", "测试成车", "123", "Vehicle", "成车分区原文", "3", "PC", "A2"),
                    Row("1", "测试充电器", "456", "Charger", "充电器分区原文", "0", "PC", "X1") },
                sectionTotals = new { Vehicle = 5, Battery = (long?)null, Charger = 0, Accessory = (long?)null },
                warnings = new[] { "离线回放只验证接入流程，不代表真实模型准确率。" }
            });
            var envelope = JsonSerializer.Serialize(new
            {
                output = new { choices = new[] { new { finish_reason = "stop", message = new { content = new[] { new { text = model } } } } } },
                usage = new { input_tokens = 123, output_tokens = 77 }
            });
            const string locate = """{"output":{"choices":[{"finish_reason":"stop","message":{"content":[{"ocr_result":{"words_info":[{"text":"测试成车（123）","rotate_rect":[200,150,120,24,12]}]}}]}}]}}""";
            var fixture = Path.Combine(run, "worker-fixture.json");
            await File.WriteAllTextAsync(fixture, JsonSerializer.Serialize(new { recognize = envelope, locate }));
            var originalResult = RecognitionParser.Parse(model) with { InputTokens = 123, OutputTokens = 77 };
            var original = new CountingOriginal(originalResult);
            var service = new EmbeddedRecognitionService(configuration, original, () => EmbeddedRecognitionPayload.Extract(cache), fixture);
            var result = await service.RecognizeAsync(image);
            Check(JsonSerializer.Serialize(result with { Elapsed = default }, Recognition.RecognitionJson.Options) ==
                JsonSerializer.Serialize(originalResult, Recognition.RecognitionJson.Options),
                "Worker adapter preserves all twelve row fields, duplicates, totals, warnings, actual-column flag and token usage");
            Check(result.Elapsed > TimeSpan.Zero, "Worker adapter retains cloud-call elapsed time");
            Check((await service.LocateAsync(image)).Single() == new TextRegion("测试成车（123）", 200, 150, 120, 24, 12),
                "Desktop adapter preserves all location coordinates and angle");
            Check(original.Calls == 0, "Successful worker requests never call the original provider");
            var enumResult = new Recognition.RecognitionResult(Enum.GetValues<Recognition.RecognitionProductType>()
                .Select(t => new Recognition.RecognizedRow("1", "原名", "名称", "123", "规格", "", t, "依据", "0", "PC", "X", [])).ToArray(),
                Enum.GetValues<Recognition.RecognitionProductType>().ToDictionary(t => t, t => (long?)(int)t), [], true);
            var mapped = EmbeddedRecognitionService.Map(enumResult);
            Check(mapped.Rows.Select(r => r.Type).SequenceEqual(Enum.GetValues<ProductType>()) && mapped.SectionTotals.Count == 5,
                "Adapter explicitly maps all four stock types and Unknown");

            var badFixture = Path.Combine(run, "bad-response.json");
            await File.WriteAllTextAsync(badFixture, JsonSerializer.Serialize(new { recognize = "{invalid envelope", locate }));
            try
            {
                await new EmbeddedRecognitionService(configuration, original, () => executable, badFixture).RecognizeAsync(image);
                throw new Exception("Worker parse error must be surfaced");
            }
            catch (BusinessException ex) { Check(ex.Message.Contains("JSON") && original.Calls == 0, "Worker failures become existing business errors and never trigger a second provider request"); }
            var blocked = new EmbeddedRecognitionService(configuration, original, () => throw new UnauthorizedAccessException("local test"));
            Check((await blocked.RecognizeAsync(image)).Rows.Count == 3 && original.Calls == 1, "Local extraction failure uses the original provider before starting a worker request");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try { await service.RecognizeAsync(image, cancelled.Token); throw new Exception("Cancellation must propagate"); }
                catch (OperationCanceledException) { Check(original.Calls == 1, "Cancelled requests do not activate a fallback provider"); }
            }

            var stock = new StockService(Path.Combine(run, "Data"));
            foreach (var row in result.Rows.DistinctBy(r => (r.Type, r.Name, r.Spec, r.Color, r.MaterialCode)))
                stock.CreateProduct(row.Type, row.Name, row.Spec, row.Color, row.MaterialCode, 0, 0, Guid.NewGuid().ToString("N"));
            owner = new MainWindow(new MainViewModel(stock)) { Left = -4000, Top = -4000, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
            Application.Current.MainWindow = owner;
            owner.Show();
            var draft = new DraftViewModel(stock, DocumentKind.Purchase);
            window = new OcrWindow(owner, draft, Path.Combine(run, "photo-dialog")) { Left = -4000, Top = -4000, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
            window.Load(image); window.ApplyCloudResult(result); window.Show(); window.UpdateLayout();
            Check(window.Rows.Count == 3 && window.Rows.All(r => r.CanReview && !r.Reviewed), "Embedded results use existing exact product matching and still require manual row review");
            Check(window.Rows[0].Product!.Id == window.Rows[1].Product!.Id && window.Rows[2].RawQuantity == "0", "Duplicate product rows remain separate and zero quantity is visible");
            Check(window.SectionTotalsList.Items.Count == 4 && window.ColumnPicker.Visibility == Visibility.Collapsed, "Main dialog shows all four totals and hides the unused offline column selector");
            window.ApplyCloudResult(result with { Rows = result.Rows.Select((r, i) => i == 1 ? r with { Type = ProductType.Unknown } : r).ToArray() });
            Check(window.SectionTotalsList.Items.Cast<string>().All(t => t.Contains("当前明细合计 待核对")),
                "Unknown stock type cannot yield misleading review subtotals by silently dropping a row");
            window.ApplyCloudResult(result);
            window.Rows[0].Quantity = "3";
            Check(window.Rows[0].RawQuantity == "2" && window.SectionTotalsList.Items.Cast<string>().First().Contains("当前明细合计 6"),
                "Quantity edits preserve the original recognized value and update the review subtotal");
            window.Rows[0].Quantity = "2";

            foreach (var size in new[] { (1360d, 900d), (1040d, 680d), (1600d, 1000d) })
                foreach (var scale in new[] { 1d, 1.25, 1.5 })
                {
                    window.Width = size.Item1 * scale; window.Height = size.Item2 * scale;
                    window.Root.LayoutTransform = new ScaleTransform(scale, scale); window.UpdateLayout();
                    var columns = window.ContentGrid.ColumnDefinitions;
                    Check(Math.Abs(columns[0].ActualWidth / (columns[0].ActualWidth + columns[2].ActualWidth) - .4) < .005 &&
                        window.ImageScroller.ActualHeight > 40 && window.ReviewScroller.ActualHeight > 40,
                        $"Main dialog provides 40/60 image/results space at {size.Item1}x{size.Item2}, layout scale {scale}");
                    var fields = Children<TextBox>(window.ReviewRows).Where(t => t.DataContext is OcrReviewRow && t.GetBindingExpression(TextBox.TextProperty) is not null).ToList();
                    var paths = fields.Select(t => t.GetBindingExpression(TextBox.TextProperty)!.ParentBinding.Path.Path).ToHashSet();
                    paths.UnionWith(Children<ProductTermBox>(window.ReviewRows).Select(t => t.GetBindingExpression(ProductTermBox.TextProperty)!.ParentBinding.Path.Path));
                    Check(new[] { "OriginalOrder", "RawName", "Name", "MaterialCode", "Spec", "Color", "SectionEvidence", "RawQuantity", "RawUnit", "Marker", "RecognitionIssues" }.All(paths.Contains) &&
                        Children<ComboBox>(window.ReviewRows).Any(c => c.GetBindingExpression(ComboBox.SelectedValueProperty)?.ParentBinding.Path.Path == "Type"),
                        $"All twelve recognized fields exist in the main review cards at {size.Item1}x{size.Item2}, scale {scale}");
                    Check(fields.All(t => t.TextWrapping == TextWrapping.Wrap && t.TranslatePoint(new System.Windows.Point(), window.ReviewRows).X + t.ActualWidth <= window.ReviewRows.ActualWidth + 1),
                        $"Long field values wrap without horizontal clipping at {size.Item1}x{size.Item2}, scale {scale}");
                }
            window.Root.LayoutTransform = Transform.Identity; window.Width = 1360; window.Height = 900; window.UpdateLayout();
            Capture(window, Path.Combine(output, "main-recognition-fields.png"));
            window.ReviewScroller.ScrollToBottom(); window.UpdateLayout();
            Check(window.ReviewScroller.VerticalOffset > 0 && window.ReviewScroller.ScrollableHeight > 0, "All cards and original field values are reachable by vertical scrolling");
            Capture(window, Path.Combine(output, "main-recognition-last-row.png"));
            try { window.AddReviewedRows(); throw new Exception("Unreviewed rows must be blocked"); }
            catch (BusinessException) { Check(draft.Lines.Count == 0 && stock.Products().All(p => p.Total == 0), "Existing review gate blocks adding unreviewed rows without changing stock"); }
            window.ApplyCloudResult(result with { ActualQuantityColumn = false });
            foreach (var row in window.Rows) row.Reviewed = true;
            try { window.AddReviewedRows(); throw new Exception("Unconfirmed actual column must be blocked"); }
            catch (BusinessException ex) { Check(ex.Message.Contains("实发列") && draft.Lines.Count == 0, "Existing actual-column gate still blocks embedded results"); }
            window.ApplyCloudResult(result);
            foreach (var row in window.Rows) row.Reviewed = true;
            Exception? addFailure = null;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); try { window.AddReviewedRows(); } catch (Exception ex) { addFailure = ex; window.Close(); } };
            timer.Start(); window.Hide(); window.ShowDialog();
            if (addFailure is not null) throw addFailure;
            Check(draft.Lines.Count == 3 && draft.Lines[0].Product.Id == draft.Lines[1].Product.Id && stock.Products().All(p => p.Total == 0),
                "Existing add-to-purchase action imports distinct source rows and leaves inventory unchanged until confirmation");
            draft.Preview();
            var document = stock.GetDocument(await draft.CommitAsync());
            Check(document.Lines.Count == 3 && document.Lines.Select(l => l.OriginalOrder).SequenceEqual(new[] { "1", "2", "1" }) &&
                document.Lines.Select(l => l.Marker).SequenceEqual(new[] { "A1", "A2", "X1" }) && stock.Products().Sum(p => p.Total) == 5,
                "Existing purchase confirmation preserves row order, markers and zero row, then records the correct stock total");
            stock.ValidateIntegrity();
            await ProductEntrySmoke.RunAsync(owner, Path.Combine(output, "product-entry"), Check, Capture);
            await ImageRegression.RunAsync(owner, stock, Path.Combine(run, "image-regression"), Check, Capture);
            File.WriteAllText(Path.Combine(output, "integration-results.json"), JsonSerializer.Serialize(new
            {
                success = true, expectedEmbedded, payloadSha256 = EmbeddedRecognitionPayload.Sha256, checks, realNetworkRequests = 0,
                userSettingsRead = false, realUserDatabaseTouched = false, testData = run,
                scalingMethod = "WPF LayoutTransform simulation; actual monitor DPI remains for manual verification"
            }, Recognition.RecognitionJson.Options));
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "integration-results.json"), JsonSerializer.Serialize(new { success = false, checks, error = ex.ToString() }, Recognition.RecognitionJson.Options));
            throw;
        }
        finally
        {
            AppContext.SetSwitch(QwenSettings.EmbeddedRecognitionSwitch, initialSwitch);
            window?.Close(); owner?.Close();
        }
    }
    private sealed class CountingOriginal(RecognitionResult result) : IRecognitionService
    {
        internal int Calls { get; private set; }
        public Task<RecognitionResult> RecognizeAsync(string confirmedImage, CancellationToken cancellationToken = default) { Calls++; return Task.FromResult(result); }
        public Task<IReadOnlyList<TextRegion>> LocateAsync(string confirmedImage, CancellationToken cancellationToken = default) { Calls++; return Task.FromResult<IReadOnlyList<TextRegion>>([]); }
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in Children<T>(child)) yield return descendant;
        }
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}

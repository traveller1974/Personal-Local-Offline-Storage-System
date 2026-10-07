using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Stock.Recognition;

namespace Stock.RecognitionLab;

/// <summary>Offline developer checks. Never reads user settings, keys, photos or databases.</summary>
internal static class LabSmoke
{
    internal static async Task RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks.Add(message); }
        var imagePath = Path.Combine(output, "offline-ui-test.png");
        DrawImage(imagePath);
        using (var image = new LabImage())
        {
            image.Load(imagePath);
            Check(image.Preview!.PixelWidth == 800 && image.Preview.PixelHeight == 500, "WPF image load preserves dimensions");
            var before = File.ReadAllBytes(image.UploadPath);
            var originalHash = image.OriginalHash;
            var rectangle = new Int32Rect(100, 100, 200, 120);
            image.CropPreview(rectangle);
            Check(before.SequenceEqual(File.ReadAllBytes(image.UploadPath)), "Crop preview does not modify upload bytes");
            foreach (var scale in new[] { .1, .5, 1, 2.5 })
                Check(LabImage.FromDisplay(new Point(100 * scale, 100 * scale), new Point(300 * scale, 220 * scale), 800 * scale, 500 * scale, 800, 500) == rectangle,
                    $"Crop coordinates map correctly at zoom {scale}");
            Check(LabImage.FromDisplay(new Point(300, 220), new Point(100, 100), 800, 500, 800, 500) == rectangle, "Reverse crop preserves selected region");
            image.Crop(rectangle); Check(image.Preview.PixelWidth == 200 && image.Preview.PixelHeight == 120, "Applied crop uses selected dimensions");
            image.Rotate(true); Check(image.Preview.PixelWidth == 120 && image.Preview.PixelHeight == 200, "Rotation updates image dimensions");
            image.Reset(); Check(image.Preview.PixelWidth == 800 && image.OriginalHash == originalHash, "Reset preserves original image and hash");
        }

        var raw = ModelResponse();
        var window = new MainWindow { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = -4000 };
        Application.Current.MainWindow = window;
        window.Show();
        try
        {
            Check(window.SamplePicker.Items.Count == 3, "Both sample baselines load from embedded resources without sidecar files");
            Check(window.SamplePicker.Items.OfType<SampleBaseline>().Select(s => s.RowCount).SequenceEqual(new[] { 17, 4 }),
                "Embedded baselines retain the actual sample row counts");
            window.LoadImage(imagePath);
            using var client = new HttpClient(new LocalReplay.Handler(raw));
            var service = new QwenRecognitionService(client, new("offline-test-placeholder", QwenConfiguration.Beijing));
            await window.CaptureAsync(service, "local-replay");
            Check(window.Rows.Count == 3 && window.Rows.All(r => r.Fields.Count == 14), "Every row displays all extraction fields and quantity column candidates");
            Check(window.Rows[2].Fields.Single(f => f.Label == "原始实发数量").Value == "0", "Zero quantity remains visible");
            Check(window.Rows[0].Fields.Single(f => f.Label == "完整规格").Value.Contains('\n'), "Full multiline specification survives display");
            Check(window.ResponseBox.Text == raw && !string.IsNullOrWhiteSpace(window.ModelTextBox.Text), "Original envelope and model text are separately visible");
            Check(window.TotalsList.Items.Count == 4, "All four section totals are visible");
            var manifest = RunExporter.Export(output, service.LastDiagnostic!, new(window.TestImage.UploadPath, window.TestImage.OriginalName,
                window.TestImage.OriginalHash, 800, 500, window.TestImage.Steps.ToArray()), "local-replay", null, "待核对", "离线自动测试");
            using (var exported = JsonDocument.Parse(File.ReadAllText(Path.Combine(manifest, "run.json"))))
            {
                Check(!exported.RootElement.GetProperty("realCallEvidence").GetBoolean(), "Replay export cannot be mistaken for real-call evidence");
                using var binary = File.OpenRead(Environment.ProcessPath!);
                Check(exported.RootElement.GetProperty("module").GetProperty("sha256").GetString() ==
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(binary)).ToLowerInvariant(),
                    "Export identifies the exact tested EXE hash for the later enabled upgrade");
            }
            Check(File.Exists(Path.Combine(manifest, "uploaded-image.png")), "Export includes exact uploaded screenshot");
            Check(!File.ReadAllText(Path.Combine(manifest, "run.json")).Contains("offline-test-placeholder"), "Export does not contain API key");

            foreach (var size in new[] { (1360d, 900d), (1040d, 680d), (1600d, 1000d) })
                foreach (var scale in new[] { 1d, 1.25, 1.5 })
                {
                    // Window dimensions grow with the simulated scale, just as physical pixels grow for WPF DIPs at monitor DPI.
                    window.Width = size.Item1 * scale; window.Height = size.Item2 * scale;
                    window.Root.LayoutTransform = new ScaleTransform(scale, scale);
                    window.UpdateLayout();
                    var columns = window.ContentGrid.ColumnDefinitions;
                    Check(Math.Abs(columns[0].ActualWidth / (columns[0].ActualWidth + columns[2].ActualWidth) - .4) < .005,
                        $"Image/results ratio stays 40/60 at {size.Item1}x{size.Item2}, layout scale {scale}");
                    Check(window.ImageScroller.ActualHeight > 40 && window.RowsList.ActualHeight > 40,
                        $"Image and results keep scrollable space at {size.Item1}x{size.Item2}, layout scale {scale}");
                    foreach (var text in Children<TextBox>(window.RowsList).Where(t => t.IsVisible && t.ActualWidth > 0))
                    {
                        Check(text.TextWrapping == TextWrapping.Wrap, "Field text wraps within row card");
                        var point = text.TranslatePoint(new Point(0, 0), window.RowsList);
                        Check(point.X + text.ActualWidth <= window.RowsList.ActualWidth + 1, "Field is not clipped horizontally");
                    }
                }
            window.Root.LayoutTransform = Transform.Identity; window.Width = 1360; window.Height = 900; window.UpdateLayout();
            Capture(window, Path.Combine(output, "fields.png"));
            window.ResultTabs.SelectedIndex = 1; window.UpdateLayout(); Capture(window, Path.Combine(output, "raw-output.png"));

            using var badClient = new HttpClient(new LocalReplay.Handler("{invalid envelope"));
            var badService = new QwenRecognitionService(badClient, new("offline-test-placeholder", QwenConfiguration.Beijing));
            await window.CaptureAsync(badService, "local-replay");
            Check(window.Rows.Count == 0 && window.ResponseBox.Text == "{invalid envelope" && window.ExportButton.IsEnabled,
                "Malformed response is viewable/exportable without yielding rows");

            var delayed = new DelayedHandler(raw);
            using var delayedClient = new HttpClient(delayed);
            var lateService = new QwenRecognitionService(delayedClient, new("offline-test-placeholder", QwenConfiguration.Beijing));
            var pending = window.CaptureAsync(lateService, "local-replay");
            await delayed.Entered.Task;
            Check(!window.RecognizeButton.IsEnabled && !window.ImageToolbar.IsEnabled, "Busy UI prevents duplicate requests and image changes");
            window.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); delayed.Release.SetResult();
            await pending;
            Check(lateService.LastDiagnostic!.Stage == "Cancelled" && window.Rows.Count == 0, "Cancelled late response never yields valid rows");
            Check(window.RecognizeButton.IsEnabled, "UI recovers after cancellation");
        }
        finally { window.Close(); }
        await WorkerSmoke.RunAsync(output, Check);
        File.WriteAllText(Path.Combine(output, "desktop-results.json"), JsonSerializer.Serialize(new
        {
            success = true, checks = checks.Distinct().ToArray(), realNetworkRequests = 0, userSettingsRead = false,
            inventoryDatabaseTouched = false, scalingMethod = "WPF LayoutTransform simulation; actual monitor DPI remains for manual verification"
        }, RecognitionJson.Options));
    }
    private sealed class DelayedHandler(string raw) : HttpMessageHandler
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.SetResult(); await Release.Task;
            return new(HttpStatusCode.OK) { Content = new StringContent(raw) };
        }
    }
    internal static string ModelResponse()
    {
        object Row(string order, string rawName, string spec, string type, string evidence, string quantity, string unit) => new
        { originalOrder = order, rawName, name = rawName, materialCode = "", spec, color = "", type, sectionEvidence = evidence, rawQuantity = quantity, rawUnit = unit, marker = "X1", issues = Array.Empty<string>() };
        var model = JsonSerializer.Serialize(new
        {
            actualQuantityColumn = true,
            rows = new[] { Row("1", "测试成车（123）", "VQS-RC20/0800/12DGK0 · 长规格换行验证\n48V / 20Ah / 型号、尺寸和标点全部保留", "Vehicle", "成车", "2", "PC"),
                Row("2", "测试成车（123）", "重复货品保留原行", "Vehicle", "成车", "3", "PC"),
                Row("1", "充电器（456）", "60V3A/68Ω20-22Ah\n2+2/千五", "Charger", "充电器", "0", "PC") },
            sectionTotals = new { Vehicle = 5, Battery = (long?)null, Charger = 0, Accessory = (long?)null },
            warnings = new[] { "离线自动测试响应，用于验证界面与解析；不代表真实模型准确率。" }
        });
        return JsonSerializer.Serialize(new { output = new { choices = new[] { new { finish_reason = "stop", message = new { content = new[] { new { text = model } } } } } }, usage = new { input_tokens = 123, output_tokens = 77 } });
    }
    private static void DrawImage(string path)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 800, 500));
            drawing.DrawRectangle(Brushes.LightGreen, null, new Rect(100, 100, 200, 120));
            drawing.DrawText(new FormattedText("OFFLINE UI TEST\nSynthetic table, no real cloud call", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"), 26, Brushes.Black, 1), new Point(30, 30));
            for (var i = 0; i < 4; i++) drawing.DrawLine(new Pen(Brushes.Gray, 2), new Point(30, 180 + 70 * i), new Point(770, 180 + 70 * i));
        }
        var bitmap = new RenderTargetBitmap(800, 500, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); Save(bitmap, path);
    }
    private static void Capture(MainWindow window, string path)
    {
        var target = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        target.Render(window); Save(target, path);
    }
    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in Children<T>(child)) yield return descendant;
        }
    }
}

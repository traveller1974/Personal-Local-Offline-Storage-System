using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Stock.Recognition;

namespace Stock.RecognitionLab;

public sealed record DisplayField(string Label, string Value);
public sealed record RowCard(string Header, IReadOnlyList<DisplayField> Fields);
public sealed record DifferenceCard(string Heading, string Detail);
internal sealed record RunImage(string Path, string OriginalName, string OriginalSha256, int Width, int Height, IReadOnlyList<string> Steps);

public partial class MainWindow : Window
{
    private readonly LabImage image = new();
    private readonly HttpClient liveClient = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private CancellationTokenSource? cancellation;
    private bool busy, closed, cropMode, fit = true, adjustingZoom;
    private Point? cropStart;
    private int generation;
    private RecognitionDiagnostic? capture;
    private RunImage? capturedImage;
    private EvaluationReport? comparison;
    private string captureSource = "";
    public ObservableCollection<RowCard> Rows { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        var options = new List<object> { "自选图片（不比较）" };
        try
        {
            using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("Stock.RecognitionLab.Samples.baseline.json")
                ?? throw new RecognitionException("内置人工基准缺失，请重新构建独立程序。");
            using var reader = new StreamReader(stream);
            options.AddRange(SampleBaseline.Parse(reader.ReadToEnd()));
        }
        catch (Exception ex) { StatusText.Text = "人工基准未能载入：" + ex.Message; }
        SamplePicker.ItemsSource = options; SamplePicker.SelectedIndex = 0;
        Loaded += (_, _) =>
        {
            if (ShowInTaskbar)
            {
                Width = Math.Max(MinWidth, Math.Min(Width, SystemParameters.WorkArea.Width * .94));
                Height = Math.Max(MinHeight, Math.Min(Height, SystemParameters.WorkArea.Height * .94));
            }
            FitPreview();
        };
        Closed += (_, _) => { closed = true; generation++; cancellation?.Cancel(); ApiKeyBox.Clear(); liveClient.Dispose(); image.Dispose(); };
    }

    private void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) { StatusText.Text = SafeMessage(ex); }
    }
    private string SafeMessage(Exception ex) => ApiKeyBox.Password.Length > 0 ? ex.Message.Replace(ApiKeyBox.Password, "[REDACTED]", StringComparison.Ordinal) : ex.Message;
    private void Choose(object sender, RoutedEventArgs e) => Try(() =>
    {
        var dialog = new OpenFileDialog { Filter = "照片|*.jpg;*.jpeg;*.png" };
        if (dialog.ShowDialog(this) == true) { LoadImage(dialog.FileName); SamplePicker.SelectedIndex = 0; }
    });
    private void OpenSample(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (SamplePicker.SelectedItem is not SampleBaseline sample) throw new RecognitionException("请先选择 sample-1 或 sample-2。");
        if (!File.Exists(sample.Image)) throw new RecognitionException("这台电脑找不到基准图片。请用“选择图片”导入对应样单，再选中基准。");
        LoadImage(sample.Image);
    });
    internal void LoadImage(string path)
    {
        if (busy) return;
        image.Load(path); ImageEdited();
        StatusText.Text = "已载入图片。请裁剪表格，确认显示内容后识别。";
    }
    private void RotateLeft(object sender, RoutedEventArgs e) => Try(() => { image.Rotate(false); ImageEdited(); });
    private void RotateRight(object sender, RoutedEventArgs e) => Try(() => { image.Rotate(true); ImageEdited(); });
    private void Reset(object sender, RoutedEventArgs e) => Try(() => { image.Reset(); ImageEdited(); });
    private void ImageEdited()
    {
        generation++; ClearCapture(); UploadConfirm.IsChecked = false; cropMode = false; FinishCrop();
        PhotoImage.Source = image.Preview; fit = true; FitPreview();
        ImageCaption.Text = $"{image.OriginalName} · 上传截图 {image.Preview!.PixelWidth} × {image.Preview.PixelHeight} · {new FileInfo(image.UploadPath).Length / 1024.0:F0} KB";
        StatusText.Text = "显示内容已更新，请重新确认截图范围。";
    }
    private void ClearCapture()
    {
        capture = null; comparison = null; capturedImage = null; captureSource = ""; Rows.Clear();
        ModelTextBox.Clear(); ResponseBox.Clear(); ParsedBox.Clear(); TotalsList.ItemsSource = null;
        ResultSummary.Text = "识别后会显示全部行级字段和单据合计。"; WarningsText.Text = "";
        ExportButton.IsEnabled = false; ReviewNotes.Clear(); ReviewDecision.SelectedIndex = 0; RefreshComparison();
    }
    private void FitImage(object sender, RoutedEventArgs e) { fit = true; FitPreview(); }
    private void ImageAreaChanged(object sender, SizeChangedEventArgs e) { if (fit) FitPreview(); }
    private void ContentAreaChanged(object sender, SizeChangedEventArgs e)
    {
        if (SummaryScroller is not null) SummaryScroller.MaxHeight = Math.Clamp(e.NewSize.Height * .25, 20, 145);
    }
    private void FitPreview()
    {
        if (image.Preview is null || ImageScroller is null) return;
        var width = Math.Max(20, ImageScroller.ActualWidth - 20);
        var height = Math.Max(20, ImageScroller.ActualHeight - 20);
        adjustingZoom = true;
        Zoom.Value = Math.Clamp(Math.Min(1, Math.Min(width / image.Preview.PixelWidth, height / image.Preview.PixelHeight)), Zoom.Minimum, Zoom.Maximum);
        adjustingZoom = false; UpdateImageSize(); ImageScroller.ScrollToHome();
    }
    private void Zoomed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PhotoImage is null || !image.HasImage) return;
        if (!adjustingZoom) fit = false;
        UpdateImageSize();
    }
    private void UpdateImageSize()
    {
        if (image.Preview is null) return;
        ImageHost.Width = PhotoImage.Width = image.Preview.PixelWidth * Zoom.Value;
        ImageHost.Height = PhotoImage.Height = image.Preview.PixelHeight * Zoom.Value;
    }

    private void EnableCrop(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (!image.HasImage) throw new RecognitionException("请先选择图片。");
        cropMode = !cropMode; FinishCrop();
        CropButton.Content = cropMode ? "取消框选" : "框选裁剪";
        StatusText.Text = cropMode ? "在左侧图片上拖动框选，松开后先预览，再应用裁剪。" : "已退出框选。";
    });
    private void CropStart(object sender, MouseButtonEventArgs e)
    {
        if (!cropMode || busy || image.Preview is null) return;
        cropStart = e.GetPosition(ImageHost); ImageHost.CaptureMouse(); e.Handled = true;
    }
    private void CropMove(object sender, MouseEventArgs e)
    {
        if (cropStart is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
        var end = e.GetPosition(ImageHost);
        var x = Math.Clamp(end.X, 0, ImageHost.Width); var y = Math.Clamp(end.Y, 0, ImageHost.Height);
        Canvas.SetLeft(Selection, Math.Min(start.X, x)); Canvas.SetTop(Selection, Math.Min(start.Y, y));
        Selection.Width = Math.Abs(x - start.X); Selection.Height = Math.Abs(y - start.Y); Selection.Visibility = Visibility.Visible;
    }
    private void CropEnd(object sender, MouseButtonEventArgs e)
    {
        if (cropStart is not Point start || image.Preview is null) return;
        var end = e.GetPosition(ImageHost); FinishCrop();
        Try(() =>
        {
            var rect = LabImage.FromDisplay(start, end, ImageHost.Width, ImageHost.Height, image.Preview.PixelWidth, image.Preview.PixelHeight);
            var preview = image.CropPreview(rect);
            var dialog = new Window { Owner = this, Title = "裁剪预览", Width = Math.Min(760, SystemParameters.WorkArea.Width * .85), Height = Math.Min(640, SystemParameters.WorkArea.Height * .85), WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var panel = new DockPanel { Margin = new Thickness(18) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var apply = new Button { Content = "应用裁剪", Style = (Style)FindResource("Primary") };
            var cancel = new Button { Content = "取消", IsCancel = true };
            apply.Click += (_, _) => dialog.DialogResult = true; buttons.Children.Add(cancel); buttons.Children.Add(apply);
            DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
            panel.Children.Add(new Image { Source = preview, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(0, 0, 0, 14) });
            dialog.Content = panel;
            if (dialog.ShowDialog() == true) { image.Crop(rect); ImageEdited(); }
        });
        e.Handled = true;
    }
    private void CropCaptureLost(object sender, MouseEventArgs e) { cropStart = null; Selection.Visibility = Visibility.Collapsed; }
    private void FinishCrop() { cropStart = null; ImageHost.ReleaseMouseCapture(); Selection.Visibility = Visibility.Collapsed; CropButton.Content = cropMode ? "取消框选" : "框选裁剪"; }

    private async void Recognize(object sender, RoutedEventArgs e)
    {
        try
        {
            if (busy) return;
            if (!image.HasImage) throw new RecognitionException("请先选择图片。");
            if (UploadConfirm.IsChecked != true) throw new RecognitionException("请先确认显示截图的上传范围。");
            var configuration = new QwenConfiguration(ApiKeyBox.Password, EndpointBox.Text.Trim(), WorkspaceBox.Text.Trim());
            configuration.Validate();
            await CaptureAsync(new QwenRecognitionService(liveClient, configuration), "real-call");
        }
        catch (Exception ex) { if (!closed) StatusText.Text = SafeMessage(ex); }
    }

    private async void Replay(object sender, RoutedEventArgs e)
    {
        try
        {
            if (busy) return;
            if (!image.HasImage) throw new RecognitionException("请先选择与本地响应对应的图片，便于对照核对。");
            var dialog = new OpenFileDialog { Filter = "本地响应或导出证据|*.json;*.txt|所有文件|*.*" };
            if (dialog.ShowDialog(this) != true) return;
            var (raw, status) = LocalReplay.Read(File.ReadAllText(dialog.FileName));
            using var client = new HttpClient(new LocalReplay.Handler(raw, status));
            await CaptureAsync(new QwenRecognitionService(client, new("local-replay-placeholder", QwenConfiguration.Beijing)), "local-replay");
        }
        catch (Exception ex) { if (!closed) StatusText.Text = SafeMessage(ex); }
    }

    internal async Task CaptureAsync(QwenRecognitionService service, string source)
    {
        var requestGeneration = ++generation;
        ClearCapture(); captureSource = source;
        capturedImage = new(image.UploadPath, image.OriginalName, image.OriginalHash, image.Preview!.PixelWidth, image.Preview.PixelHeight, image.Steps.ToArray());
        using var cts = new CancellationTokenSource(); cancellation = cts;
        ConnectionSettings.IsExpanded = false;
        SetBusy(true);
        StatusText.Text = source == "real-call" ? "正在调用百炼 qwen3.5-ocr，最多等待90秒；可取消，没有自动重试。" : "正在回放本地响应，没有发送网络请求。";
        try { await service.RecognizeAsync(capturedImage.Path, cts.Token); }
        catch (Exception) when (service.LastDiagnostic is not null) { /* The completed diagnostic carries the precise failure stage. */ }
        finally
        {
            if (!closed && requestGeneration == generation && service.LastDiagnostic is not null) ApplyCapture(service.LastDiagnostic, source);
            if (!closed) SetBusy(false);
            if (ReferenceEquals(cancellation, cts)) cancellation = null;
        }
    }
    private void SetBusy(bool value)
    {
        busy = value; ConnectionSettings.IsEnabled = ImageToolbar.IsEnabled = ReplayButton.IsEnabled = RecognizeButton.IsEnabled = !value;
        ExportButton.IsEnabled = !value && capture is not null;
        CancelButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Cancel(object sender, RoutedEventArgs e) { cancellation?.Cancel(); StatusText.Text = "已请求取消。迟到响应不会作为有效识别结果显示。"; }

    internal void ApplyCapture(RecognitionDiagnostic diagnostic, string source)
    {
        capture = diagnostic; captureSource = source;
        if (capturedImage is null && image.Preview is not null)
            capturedImage = new(image.UploadPath, image.OriginalName, image.OriginalHash, image.Preview.PixelWidth, image.Preview.PixelHeight, image.Steps.ToArray());
        ResponseBox.Text = diagnostic.RawResponse; ModelTextBox.Text = diagnostic.ModelText;
        ParsedBox.Text = diagnostic.Result is null ? "未得到有效解析结果。" : JsonSerializer.Serialize(diagnostic.Result, RecognitionJson.Options);
        Rows.Clear();
        if (diagnostic.Result is RecognitionResult result)
        {
            for (var i = 0; i < result.Rows.Count; i++) Rows.Add(Card(result.Rows[i], i + 1));
            var sums = SampleEvaluator.CalculateTotals(result);
            TotalsList.ItemsSource = Enum.GetValues<RecognitionProductType>().Where(t => t != RecognitionProductType.Unknown).Select(t =>
                $"{TypeLabel(t)}：识别合计 {Number(result.SectionTotals.GetValueOrDefault(t))} · 明细计算 {Number(sums.GetValueOrDefault(t))}").ToArray();
            ResultSummary.Text = $"{result.Rows.Count} 行 · 实发列{(result.ActualQuantityColumn ? "已确认" : "未确认")} · {diagnostic.Elapsed.TotalSeconds:F1}秒 · 输入Token {Number(result.InputTokens)} / 输出Token {Number(result.OutputTokens)}";
            WarningsText.Text = string.Join("；", result.Warnings);
            ResultTabs.SelectedIndex = 0;
        }
        else { ResultSummary.Text = "本次未得到有效解析结果，请查看原始输出。"; ResultTabs.SelectedIndex = 1; }
        RefreshComparison(); ExportButton.IsEnabled = true;
        var prefix = source == "real-call" ? "真实调用" : "本地回放（未联网）";
        StatusText.Text = diagnostic.Error is null ? $"{prefix}已完成。请对照图片核对全部字段，再导出结果与意见。" : $"{prefix}：{diagnostic.Error} 失败阶段：{StageLabel(diagnostic.Stage)}。原始响应可查看并导出。";
    }
    private static string Number(long? value) => value?.ToString() ?? "未返回／未知";
    internal static string TypeLabel(RecognitionProductType type) => type switch
    { RecognitionProductType.Vehicle => "成车", RecognitionProductType.Battery => "电池", RecognitionProductType.Charger => "充电器", RecognitionProductType.Accessory => "附件", _ => "未知类型" };
    internal static string FieldLabel(string field) => field switch
    { "originalOrder" => "原序号", "rawName" => "原始名称", "name" => "解析名称", "materialCode" => "物料编码", "spec" => "完整规格", "color" => "颜色", "type" => "类型", "sectionEvidence" => "分区依据", "rawQuantity" => "原始实发数量", "rawUnit" => "原单位", "marker" => "标识", "issues" => "问题提示", _ => field };
    private static string StageLabel(string stage) => stage switch
    { "Validation" => "配置校验", "ImageRead" => "图片读取与大小校验", "Transport" => "网络请求", "Timeout" => "超时", "HttpError" => "服务拒绝请求", "Envelope" => "服务响应结构", "ModelParse" => "模型输出解析", "Cancelled" => "已取消", _ => stage };
    private static RowCard Card(RecognizedRow row, int index)
    {
        string V(string value) => value.Length == 0 ? "（空）" : value;
        return new($"第 {index} 行 · {TypeLabel(row.Type)} · 原序号 {V(row.OriginalOrder)}", [
            new("原序号", V(row.OriginalOrder)), new("原始名称", V(row.RawName)), new("解析名称", V(row.Name)),
            new("物料编码", V(row.MaterialCode)), new("完整规格", V(row.Spec)), new("颜色", V(row.Color)),
            new("类型", $"{TypeLabel(row.Type)}（{row.Type}）"), new("分区依据", V(row.SectionEvidence)),
            new("原始实发数量", V(row.RawQuantity)), new("原单位", V(row.RawUnit)), new("标识", V(row.Marker)),
            new("问题提示", row.Issues.Count == 0 ? "无" : string.Join("\n", row.Issues))]);
    }
    private void SampleChanged(object sender, SelectionChangedEventArgs e) { if (ComparisonSummary is not null) RefreshComparison(); }
    private void RefreshComparison()
    {
        comparison = null; DifferenceList.ItemsSource = null; PendingText.Text = "";
        if (capture?.Result is not RecognitionResult result || SamplePicker.SelectedItem is not SampleBaseline baseline)
        { ComparisonSummary.Text = "选中对应样单并取得有效识别结果后，显示逐字段差异。自选图片由你对照原图核对。"; return; }
        comparison = SampleEvaluator.Evaluate(baseline, result, capture.ModelText);
        ComparisonSummary.Text = comparison.Summary;
        PendingText.Text = "人工基准待确认：" + (comparison.PendingHumanFields.Count == 0 ? "无" : string.Join("；", comparison.PendingHumanFields.Select(p => $"第{p.Row}行 {FieldLabel(p.Field)}"))) + "。这些字段不计为正确，也不使用模型输出补作标准答案。";
        DifferenceList.ItemsSource = comparison.Differences.Select(d => new DifferenceCard($"第{d.Row}行 · {FieldLabel(d.Field)} · {d.Category}",
            $"人工基准：{Display(d.Expected)}\n模型原值：{Display(d.Model)}\n解析结果：{Display(d.Parsed)}")).ToArray();
    }
    private static string Display(string value) => value.Length == 0 ? "（空）" : value;
    private void Export(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (capture is null || capturedImage is null) throw new RecognitionException("请先完成一次识别或本地回放。");
        var dialog = new OpenFolderDialog { Title = "选择导出目录（每次创建独立测试记录）", InitialDirectory = AppContext.BaseDirectory };
        if (dialog.ShowDialog(this) != true) return;
        var decision = (ReviewDecision.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "待核对";
        var output = RunExporter.Export(dialog.FolderName, capture, capturedImage, captureSource, comparison, decision, ReviewNotes.Text);
        StatusText.Text = "已导出本次截图、原始响应、解析结果、对比与人工意见：" + output;
    });
    private void Dragged(object sender, DragEventArgs e) { e.Effects = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void Dropped(object sender, DragEventArgs e) => Try(() =>
    {
        if (busy) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length != 1) throw new RecognitionException("每次请拖入一张照片。");
        LoadImage(paths[0]); SamplePicker.SelectedIndex = 0;
    });
    internal LabImage TestImage => image;
}

internal static class LocalReplay
{
    internal static (string Raw, HttpStatusCode Status) Read(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (text, HttpStatusCode.OK);
            if (root.TryGetProperty("diagnostic", out var diagnostic)) root = diagnostic;
            if (root.TryGetProperty("rawResponse", out var raw))
            {
                var status = root.TryGetProperty("httpStatus", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var n) ? (HttpStatusCode)n : HttpStatusCode.OK;
                return (raw.GetString() ?? "", status);
            }
            if (root.TryGetProperty("rows", out _)) text = JsonSerializer.Serialize(new { output = new { choices = new[] { new { finish_reason = "stop", message = new { content = new[] { new { text } } } } } } });
        }
        catch (JsonException) { }
        return (text, HttpStatusCode.OK);
    }
    internal sealed class Handler(string text, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(text) });
    }
}

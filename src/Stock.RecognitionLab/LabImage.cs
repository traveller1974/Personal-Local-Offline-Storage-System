using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Stock.Recognition;

namespace Stock.RecognitionLab;

/// <summary>WPF-only image preparation; no database, Python worker or desktop application dependency.</summary>
internal sealed class LabImage : IDisposable
{
    private readonly string session = Path.Combine(Path.GetTempPath(), "Stock.RecognitionLab-" + Guid.NewGuid().ToString("N"));
    private BitmapSource? original, current;
    internal BitmapSource? Preview { get; private set; }
    internal string UploadPath { get; private set; } = "";
    internal string OriginalHash { get; private set; } = "";
    internal string OriginalName { get; private set; } = "";
    internal List<string> Steps { get; } = [];
    internal bool HasImage => current is not null;

    internal void Load(string path)
    {
        if (!new[] { ".png", ".jpg", ".jpeg" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new RecognitionException("请选择 JPG 或 PNG 照片。");
        if (new FileInfo(path).Length > 100 * 1024 * 1024) throw new RecognitionException("照片超过100MB，请先缩小后导入。");
        var bytes = File.ReadAllBytes(path);
        var candidate = Decode(bytes);
        if ((long)candidate.PixelWidth * candidate.PixelHeight > 60_000_000)
            throw new RecognitionException("照片超过6000万像素，请先缩小后导入。");
        Prepare(candidate);
        original = candidate;
        OriginalHash = Convert.ToHexString(SHA256.HashData(bytes));
        OriginalName = Path.GetFileName(path);
        Steps.Clear(); Steps.Add("载入原图");
    }

    internal void Rotate(bool clockwise)
    {
        if (current is null) throw new RecognitionException("请先导入图片。");
        var rotated = new TransformedBitmap(current, new RotateTransform(clockwise ? 90 : -90));
        rotated.Freeze(); Prepare(rotated); Steps.Add(clockwise ? "向右旋转90度" : "向左旋转90度");
    }
    internal BitmapSource CropPreview(Int32Rect rectangle)
    {
        if (current is null) throw new RecognitionException("请先导入图片。");
        if (rectangle.Width < 5 || rectangle.Height < 5 || rectangle.X < 0 || rectangle.Y < 0 ||
            rectangle.X + rectangle.Width > current.PixelWidth || rectangle.Y + rectangle.Height > current.PixelHeight)
            throw new RecognitionException("请框选照片内至少5×5像素的区域。");
        var cropped = new CroppedBitmap(current, rectangle); cropped.Freeze(); return cropped;
    }
    internal void Crop(Int32Rect rectangle)
    {
        Prepare(CropPreview(rectangle));
        Steps.Add($"裁剪 x={rectangle.X}, y={rectangle.Y}, w={rectangle.Width}, h={rectangle.Height}");
    }
    internal void Reset()
    {
        if (original is null) throw new RecognitionException("请先导入图片。");
        Prepare(original); Steps.Clear(); Steps.Add("恢复原图");
    }

    private void Prepare(BitmapSource candidate)
    {
        if (candidate.PixelWidth <= 10 || candidate.PixelHeight <= 10 ||
            Math.Max((double)candidate.PixelWidth / candidate.PixelHeight, (double)candidate.PixelHeight / candidate.PixelWidth) > 200)
            throw new RecognitionException("截图两边须大于10像素，宽高比不能超过200:1。");
        var scale = Math.Min(1, Math.Sqrt(15680000.0 / ((long)candidate.PixelWidth * candidate.PixelHeight)));
        if (scale < 1)
        {
            candidate = new TransformedBitmap(candidate, new ScaleTransform(scale, scale)); candidate.Freeze();
        }
        var encoded = Encode(candidate, false);
        var extension = ".png";
        if (!Fits(encoded)) { encoded = Encode(candidate, true); extension = ".jpg"; }
        if (!Fits(encoded)) throw new RecognitionException("截图编码超过允许大小，请缩小照片后重新选择。");
        Directory.CreateDirectory(session);
        var path = Path.Combine(session, Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, encoded);
        // The displayed frame is decoded from the exact bytes submitted to the model, including JPEG compression.
        Preview = Decode(encoded); current = candidate; UploadPath = path;
    }
    private static bool Fits(byte[] bytes) => bytes.Length <= 7 * 1024 * 1024 && 4L * ((bytes.Length + 2L) / 3) <= 10 * 1024 * 1024;
    private static byte[] Encode(BitmapSource bitmap, bool jpeg)
    {
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = 95 } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        bitmap.Freeze(); return bitmap;
    }
    internal static Int32Rect FromDisplay(Point start, Point end, double width, double height, int pixelsWide, int pixelsHigh)
    {
        if (width <= 0 || height <= 0) throw new RecognitionException("图片显示范围无效。");
        var left = (int)Math.Floor(Math.Clamp(Math.Min(start.X, end.X) / width, 0, 1) * pixelsWide);
        var top = (int)Math.Floor(Math.Clamp(Math.Min(start.Y, end.Y) / height, 0, 1) * pixelsHigh);
        var right = (int)Math.Ceiling(Math.Clamp(Math.Max(start.X, end.X) / width, 0, 1) * pixelsWide);
        var bottom = (int)Math.Ceiling(Math.Clamp(Math.Max(start.Y, end.Y) / height, 0, 1) * pixelsHigh);
        if (right - left < 5 || bottom - top < 5) throw new RecognitionException("框选范围过小，请选择至少5×5像素。");
        return new(left, top, right - left, bottom - top);
    }
    public void Dispose()
    {
        var expectedPrefix = Path.GetFullPath(Path.GetTempPath()) + (Path.EndsInDirectorySeparator(Path.GetTempPath()) ? "" : Path.DirectorySeparatorChar) + "Stock.RecognitionLab-";
        if (Path.GetFullPath(session).StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            try { if (Directory.Exists(session)) Directory.Delete(session, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

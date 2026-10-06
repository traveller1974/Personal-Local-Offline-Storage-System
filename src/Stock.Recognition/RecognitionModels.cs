using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stock.Recognition;

public sealed class RecognitionException(string message) : Exception(message);
public enum RecognitionProductType { Unknown, Vehicle, Battery, Charger, Accessory }
public sealed record RecognizedRow(string OriginalOrder, string RawName, string Name, string MaterialCode,
    string Spec, string Color, RecognitionProductType Type, string SectionEvidence, string RawQuantity,
    string RawUnit, string Marker, IReadOnlyList<string> Issues);
public sealed record RecognitionResult(IReadOnlyList<RecognizedRow> Rows,
    IReadOnlyDictionary<RecognitionProductType, long?> SectionTotals, IReadOnlyList<string> Warnings,
    bool ActualQuantityColumn, TimeSpan Elapsed = default, long? InputTokens = null, long? OutputTokens = null);
public sealed record TextRegion(string Text, double CenterX, double CenterY, double Width, double Height, double Angle);
public interface IRecognitionService
{
    Task<RecognitionResult> RecognizeAsync(string confirmedImage, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TextRegion>> LocateAsync(string confirmedImage, CancellationToken cancellationToken = default);
}

/// <summary>A completed call snapshot. Request headers, API keys and image Base64 are never recorded.</summary>
public sealed record RecognitionDiagnostic(DateTimeOffset StartedAt, string Operation, string Model,
    string PromptVersion, string PromptSha256, string Endpoint, string ImageSha256, TimeSpan Elapsed,
    int? HttpStatus, string RawResponse, string ModelText, long? InputTokens, long? OutputTokens,
    string Stage, string? Error, RecognitionResult? Result, IReadOnlyList<TextRegion>? Regions);

public static class RecognitionJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };
}

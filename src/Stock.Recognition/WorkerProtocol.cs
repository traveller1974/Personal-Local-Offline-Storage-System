using System.Text.Json;

namespace Stock.Recognition;

/// <summary>One request and one response over redirected standard streams. Credentials never enter process arguments.</summary>
public sealed record RecognitionWorkerRequest(string RequestId, string Operation, string ConfirmedImage,
    QwenConfiguration Configuration, int ProtocolVersion = 2, RecognitionConfiguration? ProviderConfiguration = null, RecognitionContext? Context = null);
public sealed record RecognitionWorkerResponse(string RequestId, bool Success, RecognitionResult? Result = null,
    IReadOnlyList<TextRegion>? Regions = null, string? Error = null, int ProtocolVersion = 2);
public static class RecognitionWorkerProtocol
{
    public const int Version = 2;
    public const string Recognize = "recognize";
    public const string Locate = "locate";
    public static JsonSerializerOptions Json { get; } = new(RecognitionJson.Options) { WriteIndented = false };
}

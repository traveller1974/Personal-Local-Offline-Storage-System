using Shared = Stock.Recognition;

namespace Stock.Core;

public sealed record RecognizedRow(string OriginalOrder, string RawName, string Name, string MaterialCode, string Spec, string Color,
    ProductType Type, string SectionEvidence, string RawQuantity, string RawUnit, string Marker, IReadOnlyList<string> Issues)
{
    public string RowId { get; init; } = "";
    public string QuantityColumn { get; init; } = "";
    public IReadOnlyList<Shared.QuantityCandidate> QuantityCandidates { get; init; } = [];
}
public sealed record RecognitionResult(IReadOnlyList<RecognizedRow> Rows, IReadOnlyDictionary<ProductType, long?> SectionTotals,
    IReadOnlyList<string> Warnings, bool ActualQuantityColumn, TimeSpan Elapsed = default, long? InputTokens = null, long? OutputTokens = null)
{
    public string Provider { get; init; } = "";
    public string PromptVersion { get; init; } = "";
}
public sealed record TextRegion(string Text, double CenterX, double CenterY, double Width, double Height, double Angle);
public interface IRecognitionService
{
    Task<RecognitionResult> RecognizeAsync(string confirmedImage, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TextRegion>> LocateAsync(string confirmedImage, CancellationToken cancellationToken = default);
}
public sealed record QwenConfiguration(string ApiKey, string Endpoint, string Workspace = "")
{
    public const string Beijing = Shared.QwenConfiguration.Beijing;
    public Uri Validate()
    {
        try { return new Shared.QwenConfiguration(ApiKey, Endpoint, Workspace).Validate(); }
        catch (Shared.RecognitionException ex) { throw new BusinessException(ex.Message); }
    }
}
// The original in-process entry point shares the parser and provider with the independent EXE.
public sealed class QwenRecognitionService(HttpClient client, QwenConfiguration configuration) : IRecognitionService
{
    public const string Model = Shared.QwenRecognitionService.Model;
    public const string Prompt = Shared.QwenRecognitionService.Prompt;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(90);
    public Shared.RecognitionContext Context { get; init; } = new();
    private Shared.QwenRecognitionService Create() => new(client, new(configuration.ApiKey, configuration.Endpoint, configuration.Workspace)) { Timeout = Timeout, Context = Context };
    public async Task<RecognitionResult> RecognizeAsync(string confirmedImage, CancellationToken cancellationToken = default)
    {
        try { return RecognitionMapping.Map(await Create().RecognizeAsync(confirmedImage, cancellationToken)); }
        catch (Shared.RecognitionException ex) { throw new BusinessException(ex.Message); }
    }
    public async Task<IReadOnlyList<TextRegion>> LocateAsync(string confirmedImage, CancellationToken cancellationToken = default)
    {
        try { return (await Create().LocateAsync(confirmedImage, cancellationToken)).Select(r => new TextRegion(r.Text, r.CenterX, r.CenterY, r.Width, r.Height, r.Angle)).ToArray(); }
        catch (Shared.RecognitionException ex) { throw new BusinessException(ex.Message); }
    }
}
public sealed class ProviderRecognitionService(HttpClient client, Shared.RecognitionConfiguration configuration, Shared.RecognitionContext? context = null) : IRecognitionService
{
    public async Task<RecognitionResult> RecognizeAsync(string confirmedImage, CancellationToken cancellationToken = default)
    {
        try { return RecognitionMapping.Map(await Shared.RecognitionProviderFactory.Create(client, configuration, context).RecognizeAsync(confirmedImage, cancellationToken)); }
        catch (Shared.RecognitionException ex) { throw new BusinessException(ex.Message); }
    }
    public async Task<IReadOnlyList<TextRegion>> LocateAsync(string confirmedImage, CancellationToken cancellationToken = default)
    {
        try { return (await Shared.RecognitionProviderFactory.Create(client, configuration, context).LocateAsync(confirmedImage, cancellationToken)).Select(r => new TextRegion(r.Text, r.CenterX, r.CenterY, r.Width, r.Height, r.Angle)).ToArray(); }
        catch (Shared.RecognitionException ex) { throw new BusinessException(ex.Message); }
    }
}
public static class RecognitionParser
{
    public static RecognitionResult Parse(string text)
    {
        try { return RecognitionMapping.Map(Shared.RecognitionParser.Parse(text)); }
        catch (Shared.RecognitionException ex) { throw new BusinessException(ex.Message); }
    }
    public static bool Quantity(string text, out long value) => Shared.RecognitionParser.Quantity(text, out value);
}
public static class RecognitionMapping
{
    public static RecognitionResult Map(Shared.RecognitionResult result) => new(
        result.Rows.Select(r => new RecognizedRow(r.OriginalOrder, r.RawName, r.Name, r.MaterialCode, r.Spec, r.Color,
            (ProductType)r.Type, r.SectionEvidence, r.RawQuantity, r.RawUnit, r.Marker, r.Issues)
            { RowId = r.RowId, QuantityColumn = r.QuantityColumn, QuantityCandidates = r.QuantityCandidates }).ToArray(),
        result.SectionTotals.ToDictionary(t => (ProductType)t.Key, t => t.Value), result.Warnings,
        result.ActualQuantityColumn, result.Elapsed, result.InputTokens, result.OutputTokens)
        { Provider = result.Provider, PromptVersion = result.PromptVersion };
}

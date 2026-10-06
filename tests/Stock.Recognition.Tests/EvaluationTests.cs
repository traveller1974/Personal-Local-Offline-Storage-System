using System.Text.Json;
using Stock.Recognition;
using Xunit;

namespace Stock.Recognition.Tests;

public sealed class EvaluationTests
{
    private static SampleBaseline Baseline()
    {
        using var json = JsonDocument.Parse("{\"originalOrder\":\"1\",\"type\":\"Vehicle\",\"rawQuantity\":\"2\",\"rawUnit\":\"PC\",\"name\":\"车\",\"spec\":null,\"materialCode\":\"123\"}");
        return new("test-sample", "unused.png", 3, new() { [RecognitionProductType.Vehicle] = 5, [RecognitionProductType.Charger] = 0 },
            [json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())]);
    }
    [Fact] public void PendingFieldsNeverCountAsCorrectAndParsingChangesAreSeparate()
    {
        var model = QwenTests.Sample(); model["rows"]![0]!["name"] = "模型给出的不同名称";
        var result = RecognitionParser.Parse(model.ToJsonString());
        var report = SampleEvaluator.Evaluate(Baseline(), result, model.ToJsonString());
        Assert.Single(report.PendingHumanFields); Assert.Equal("spec", report.PendingHumanFields[0].Field);
        Assert.Equal(2, report.CheckedFields); Assert.Equal(1d, report.FieldAccuracy);
        Assert.Contains(report.Differences, d => d.Field == "name" && d.Parsed == "车" && d.Category.Contains("解析修正"));
        Assert.Contains(report.ParsingChanges, d => d.Field == "name"); Assert.True(report.CalculatedTotalsMatch);
    }
    [Fact] public void ReturnedAndCalculatedTotalsAreComparedIndependently()
    {
        var model = QwenTests.Sample(); model["sectionTotals"]!["Vehicle"] = 99;
        var report = SampleEvaluator.Evaluate(Baseline(), RecognitionParser.Parse(model.ToJsonString()), model.ToJsonString());
        Assert.False(report.ReturnedTotalsMatch); Assert.True(report.CalculatedTotalsMatch);
    }
    [Fact] public void InvalidQuantitiesAndUnknownTypesCannotProduceMatchingTotals()
    {
        var model = QwenTests.Sample(); model["rows"]![0]!["rawQuantity"] = "bad";
        var report = SampleEvaluator.Evaluate(Baseline(), RecognitionParser.Parse(model.ToJsonString()), model.ToJsonString());
        Assert.Null(report.CalculatedTotals[RecognitionProductType.Vehicle]); Assert.False(report.CalculatedTotalsMatch);
        model["rows"]![0]!["type"] = "Unknown";
        Assert.All(SampleEvaluator.CalculateTotals(RecognitionParser.Parse(model.ToJsonString())).Values, value => Assert.Null(value));
    }
    [Fact] public void MissingActualColumnAndExtraRowsFailCriticalChecks()
    {
        var model = QwenTests.Sample(); model["actualQuantityColumn"] = false;
        Assert.False(SampleEvaluator.Evaluate(Baseline(), RecognitionParser.Parse(model.ToJsonString()), model.ToJsonString()).CriticalFieldsAllCorrect);
        model["actualQuantityColumn"] = true; model["rows"]!.AsArray().Add(model["rows"]![0]!.DeepClone());
        Assert.False(SampleEvaluator.Evaluate(Baseline(), RecognitionParser.Parse(model.ToJsonString()), model.ToJsonString()).CriticalFieldsAllCorrect);
    }
}

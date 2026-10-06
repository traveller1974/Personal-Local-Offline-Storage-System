using System.Text.Json.Nodes;
using Stock.Core;
using Xunit;

namespace Stock.Tests;

public sealed class RecognitionCompatibilityTests
{
    [Fact] public void OptionalFieldsAndAbsentTotalsKeepEverySourceRow()
    {
        var sample=QwenTests.Sample();sample.Remove("warnings");sample.Remove("sectionTotals");
        foreach(var row in sample["rows"]!.AsArray()){row!.AsObject().Remove("issues");row.AsObject().Remove("marker");}
        var result=RecognitionParser.Parse(sample.ToJsonString());Assert.Equal(3,result.Rows.Count);
        Assert.All(result.SectionTotals.Values,n=>Assert.Null(n));Assert.Equal("0",result.Rows[2].RawQuantity);
        Assert.Equal(result.Rows[0].Name,result.Rows[1].Name);Assert.Equal("",result.Rows[0].Marker);
    }
    [Theory] [InlineData("name")] [InlineData("rawQuantity")] [InlineData("type")] [InlineData("spec")] [InlineData("rawUnit")]
    public void MissingCriticalFieldNeverDropsTheRowOrInventsAValue(string field)
    {
        var sample=QwenTests.Sample();var row=sample["rows"]![2]!.AsObject();row.Remove(field);
        var result=RecognitionParser.Parse(sample.ToJsonString());Assert.Equal(3,result.Rows.Count);
        Assert.Contains(result.Rows[2].Issues,n=>n.Contains(field));
        if(field=="rawQuantity")Assert.Equal("",result.Rows[2].RawQuantity);
        if(field=="type")Assert.Equal(ProductType.Unknown,result.Rows[2].Type);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void MissingActualColumnIsNeverInferredFromQuantitiesOrTotals(bool nullValue)
    {
        var sample=QwenTests.Sample();if(nullValue)sample["actualQuantityColumn"]=null;else sample.Remove("actualQuantityColumn");
        var result=RecognitionParser.Parse(sample.ToJsonString());Assert.False(result.ActualQuantityColumn);Assert.Equal(3,result.Rows.Count);
        Assert.Contains(result.Warnings,w=>w.Contains("不能加入"));
    }
    [Fact] public void IntegerNumbersAndStringTotalsAreAcceptedWithoutChangingOrder()
    {
        var sample=QwenTests.Sample();sample["rows"]![0]!["originalOrder"]=1;sample["rows"]![0]!["rawQuantity"]=2;
        sample["sectionTotals"]!["Vehicle"]="5";sample["sectionTotals"]!.AsObject().Remove("Battery");
        var result=RecognitionParser.Parse(sample.ToJsonString());Assert.Equal("1",result.Rows[0].OriginalOrder);
        Assert.Equal("2",result.Rows[0].RawQuantity);Assert.Equal(5,result.SectionTotals[ProductType.Vehicle]);Assert.Null(result.SectionTotals[ProductType.Battery]);
    }
    [Theory] [InlineData("-1")] [InlineData("1.0")] [InlineData("1e3")] [InlineData("2147483648")] [InlineData("false")]
    public void InvalidScalarQuantityIsRetainedAndNotMadeIntoZero(string json)
    {
        var sample=QwenTests.Sample();sample["rows"]![0]!["rawQuantity"]=JsonNode.Parse(json);
        var result=RecognitionParser.Parse(sample.ToJsonString());Assert.Equal(json,result.Rows[0].RawQuantity);
        Assert.False(RecognitionParser.Quantity(result.Rows[0].RawQuantity,out _));Assert.NotEmpty(result.Rows[0].Issues);
    }
    [Fact] public void ExtraFieldsDoNotAffectBusinessMapping()
    {
        var sample=QwenTests.Sample();sample["privateCustomerData"]="must-not-appear-in-warning";
        sample["rows"]![0]!["plannedQuantity"]="99";sample["sectionTotals"]!["other"]=88;
        var result=RecognitionParser.Parse(sample.ToJsonString());Assert.Equal("2",result.Rows[0].RawQuantity);
        Assert.Contains(result.Warnings,w=>w.Contains("额外字段"));Assert.Contains(result.Rows[0].Issues,w=>w.Contains("额外字段"));
        Assert.DoesNotContain("privateCustomerData",string.Join("",result.Warnings));Assert.DoesNotContain("must-not-appear",string.Join("",result.Warnings));
    }
    [Theory] [InlineData("name", "7")] [InlineData("issues","\"bad\"")] [InlineData("rawQuantity","{}")]
    public void WrongRowFieldShapeHasAnExactPath(string field,string json)
    {
        var sample=QwenTests.Sample();sample["rows"]![1]![field]=JsonNode.Parse(json);
        var error=Assert.Throws<BusinessException>(()=>RecognitionParser.Parse(sample.ToJsonString()));Assert.Contains($"rows[1].{field}",error.Message);
    }
    [Fact] public void DuplicateFieldsAreRejectedAtAllLevelsIncludingUnknownFields()
    {
        var sample=QwenTests.Sample().ToJsonString();
        foreach(var json in new[]{sample.Replace("\"actualQuantityColumn\":true","\"actualQuantityColumn\":true,\"actualQuantityColumn\":false"),
            sample.Replace("\"originalOrder\":\"1\"","\"originalOrder\":\"1\",\"originalOrder\":\"2\""),
            sample[..^1]+",\"extra\":{\"secretCustomer\":1,\"secretCustomer\":2}}"})
        {var error=Assert.Throws<BusinessException>(()=>RecognitionParser.Parse(json));Assert.Contains("重复字段",error.Message);Assert.DoesNotContain("secretCustomer",error.Message);}
    }
    [Theory] [InlineData("true")] [InlineData("null")] [InlineData("\"row\"")]
    public void InvalidRowElementRejectsTheEntireResponse(string json)
    {
        var sample=QwenTests.Sample();sample["rows"]!.AsArray()[1]=JsonNode.Parse(json);
        Assert.Contains("rows[1]",Assert.Throws<BusinessException>(()=>RecognitionParser.Parse(sample.ToJsonString())).Message);
    }
    [Theory] [InlineData("false")] [InlineData("1.5")] [InlineData("2147483648")] [InlineData("\"unreadable\"")]
    public void InvalidTotalsAreUnknownWithAWarning(string json)
    {
        var sample=QwenTests.Sample();sample["sectionTotals"]!["Vehicle"]=JsonNode.Parse(json);
        var result=RecognitionParser.Parse(sample.ToJsonString());Assert.Null(result.SectionTotals[ProductType.Vehicle]);
        Assert.Equal("2",result.Rows[0].RawQuantity);Assert.Contains(result.Warnings,w=>w.Contains("sectionTotals.Vehicle"));
    }
    [Theory] [InlineData("```json\r\n")] [InlineData("```JSON\n")] [InlineData("```\r\n")]
    public void CompleteCommonCodeFencesAreAccepted(string prefix)=>Assert.Equal(3,RecognitionParser.Parse(prefix+QwenTests.Sample().ToJsonString()+"\r\n```").Rows.Count);
    [Fact] public void WrongActualColumnTypeCannotBeMadeTrue()
    {var sample=QwenTests.Sample();sample["actualQuantityColumn"]="true";Assert.Contains("actualQuantityColumn",Assert.Throws<BusinessException>(()=>RecognitionParser.Parse(sample.ToJsonString())).Message);}
}

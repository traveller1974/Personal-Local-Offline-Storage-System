using System.Text.Json.Nodes;
using Stock.Recognition;
using Xunit;

namespace Stock.Recognition.Tests;

public sealed class QuantityColumnTests
{
    private static RecognitionResult Parse(string raw,string column,bool confirmed,params (string Header,string Value)[] candidates)
    {
        var sample=QwenTests.Sample();sample["actualQuantityColumn"]=confirmed;
        var row=sample["rows"]![0]!.AsObject();row["rawQuantity"]=raw;row["quantityColumn"]=column;
        row["quantityCandidates"]=new JsonArray(candidates.Select(c=>(JsonNode)new JsonObject{["header"]=c.Header,["value"]=c.Value}).ToArray());
        return RecognitionParser.Parse(sample.ToJsonString());
    }

    [Theory] [InlineData("20","18","2")] [InlineData("30","28","2")] [InlineData("5","0","5")]
    public void ExplicitActualCandidateReplacesPlannedQuantityAndKeepsSourceEvidence(string planned,string actual,string shortage)
    {
        var result=Parse(planned,"实发",true,("计划",planned),("实发",actual),("欠发",shortage));
        Assert.Equal(actual,result.Rows[0].RawQuantity);Assert.Equal("实发",result.Rows[0].QuantityColumn);
        Assert.Equal(planned,result.Rows[0].QuantityCandidates[0].Value);
        Assert.Contains(result.Rows[0].Issues,i=>i.Contains("已按实发列"));Assert.Equal(3,result.Rows.Count);
    }

    [Fact] public void UnconfirmedColumnIsNeverPromotedByCandidateValues()
    {
        var result=Parse("20","实发",false,("计划","20"),("实发","18"),("欠发","2"));
        Assert.False(result.ActualQuantityColumn);Assert.Equal("20",result.Rows[0].RawQuantity);
    }

    [Theory] [InlineData("18","17")] [InlineData("18","")] [InlineData("invalid","invalid")]
    public void ConflictingOrInvalidActualCandidatesStayVisibleWithoutGuessing(string first,string second)
    {
        var result=Parse("20","实发",true,("实发",first),("实发",second));
        Assert.Equal("20",result.Rows[0].RawQuantity);Assert.Contains(result.Rows[0].Issues,i=>i.Contains("有矛盾或看不清"));
    }

    [Fact] public void InconsistentRowArithmeticAddsAdviceAndNeverCalculatesAMissingValue()
    {
        var result=Parse("12","实发",true,("计划","12"),("实发","12"),("欠发","2"));
        Assert.Equal("12",result.Rows[0].RawQuantity);Assert.Contains(result.Rows[0].Issues,i=>i.Contains("没有按差额改数量"));
    }

    [Fact] public void MissingActualCandidateCannotBeFilledBySubtractingShortage()
    {
        var result=Parse("","实发",true,("计划","12"),("欠发","2"));
        Assert.Equal("",result.Rows[0].RawQuantity);Assert.NotEmpty(result.Rows[0].Issues);
    }

    [Fact] public void WhitespaceInHeaderAndRepeatedIdenticalEvidenceDoNotLoseTheActualValue()
    {
        var result=Parse("20","实发数量",true,("实 发 数量","18"),("实发数量","18"));
        Assert.Equal("18",result.Rows[0].RawQuantity);
    }

    [Theory] [InlineData("计划")] [InlineData("欠发")]
    public void NonActualSelectionRemainsEditableAndIsFlagged(string column)
    {
        var result=Parse("20",column,true,(column,"20"));
        Assert.Equal("20",result.Rows[0].RawQuantity);Assert.Contains(result.Rows[0].Issues,i=>i.Contains("请对照照片填写"));
    }

    [Fact] public void SimilarHeaderTextDoesNotCountAsAnActualColumn()
    {
        var result=Parse("20","实发",true,("非实发","18"),("实发合计","18"));
        Assert.Equal("20",result.Rows[0].RawQuantity);
    }
}

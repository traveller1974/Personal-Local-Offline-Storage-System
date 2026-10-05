using Stock.Core;
using Xunit;

namespace Stock.Tests;

public class InvoiceTests
{
    private static OcrBlock B(string text, double x, double y, double confidence = .99) => new(text, confidence, [[x-25,y-10],[x+25,y-10],[x+25,y+10],[x-25,y+10]]);
    private static OcrResponse Invoice(params OcrBlock[] blocks) => new("test", true, 1000, 1000, blocks);
    [Fact] public void A13_QuantityIsNotPriceAndTotalIsExcluded()
    {
        var data = Invoice(B("品名",100,100),B("规格",300,100),B("数量",500,100),B("单价",700,100),B("金额",900,100),
            B("螺丝",100,150),B("M8",300,150),B("5",500,150),B("12",700,150),B("60",900,150),B("合计",100,200),B("60",900,200));
        var result = InvoiceParser.Parse(data); var row = Assert.Single(result.Rows);
        Assert.Equal("5",row.RawQuantity); Assert.Equal("螺丝",row.Name); Assert.Equal("M8",row.Spec);
    }
    [Fact] public void A14_MultipleQuantityColumnsRequireExplicitSelection()
    {
        var data = Invoice(B("商品名称",100,100),B("数量",400,100),B("实收数量",700,100),B("螺丝",100,150),B("5",400,150),B("4",700,150));
        var unresolved = InvoiceParser.Parse(data); Assert.True(unresolved.NeedsColumn); Assert.Empty(unresolved.Rows);
        Assert.Equal("4",Assert.Single(InvoiceParser.Parse(data,"1").Rows).RawQuantity);
    }
    [Fact] public void A14_MissingDecimalAndLowConfidenceRemainFlagged()
    {
        var data = Invoice(B("货品名称",100,100),B("型号",300,100),B("实收数量",500,100),B("螺丝",100,150,.70),B("M8",300,150),B("1.5",500,150),B("螺帽",100,200));
        var rows = InvoiceParser.Parse(data).Rows;
        Assert.Equal(2,rows.Count); Assert.Contains("置信度低",rows[0].Warning); Assert.Contains("不是有效整数",rows[0].Warning); Assert.Contains("数量缺失",rows[1].Warning);
    }
    [Fact] public void A13_NoQuantityHeaderDoesNotInferAmount()
    { var r=InvoiceParser.Parse(Invoice(B("品名",100,100),B("金额",500,100),B("螺丝",100,150),B("500",500,150))); Assert.Empty(r.Rows); Assert.Contains("不会",r.Message); }
    [Theory] [InlineData("",false)] [InlineData("-1",false)] [InlineData("1.5",false)] [InlineData("1e3",false)] [InlineData("2147483648",false)] [InlineData("５",false)] [InlineData("12",true)]
    public void A09_IntegerTextIsStrict(string text,bool valid) => Assert.Equal(valid,IntegerInput.TryParse(text,1,out _));
}

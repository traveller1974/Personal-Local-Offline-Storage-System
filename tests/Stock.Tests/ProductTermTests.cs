using Stock.Core;
using Xunit;

namespace Stock.Tests;

public sealed class ProductTermTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "product-terms-" + Guid.NewGuid().ToString("N"));
    private readonly StockService service;
    public ProductTermTests() => service = new StockService(root);
    private long Add(string name, string spec = "", string color = "") =>
        service.CreateProduct(ProductType.Vehicle, name, spec, color, "", 0, 0, Guid.NewGuid().ToString("N"));

    [Fact] public void KeywordsMatchWholeFieldAndRankExactThenPrefix()
    {
        Add("超级飞驰"); Add("飞驰二代"); Add("飞驰一代"); Add("飞驰"); Add("其他货品");
        Assert.Equal(new[] { "飞驰", "飞驰一代", "飞驰二代", "超级飞驰" }, service.ProductTerms("Name", "  飞驰  "));
    }
    [Fact] public void EmptyColorShowsDistinctNonemptyTerms()
    {
        Add("一代", color: "薄荷绿"); Add("二代", color: "天蓝"); Add("三代", color: "薄荷绿"); Add("四代");
        Assert.Equal(new[] { "天蓝", "薄荷绿" }, service.ProductTerms("Color"));
        Assert.Equal(new[] { "薄荷绿" }, service.ProductTerms("Color", "荷"));
    }
    [Fact] public void SpecsKeepFullMultilineValueAndIgnoreEmptyValues()
    {
        const string spec = "48V / 20Ah\n规格第二行";
        Add("一代", spec); Add("二代", spec); Add("三代");
        Assert.Equal(new[] { spec }, service.ProductTerms("Spec", "20aH"));
    }
    [Fact] public void SqlAndWildcardCharactersAreLiteralSearchText()
    {
        Add("飞驰%_特别版"); Add("O'Brien"); Add("飞驰正常版");
        Assert.Equal(new[] { "飞驰%_特别版" }, service.ProductTerms("Name", "%_"));
        Assert.Equal(new[] { "O'Brien" }, service.ProductTerms("Name", "o'brien"));
        Assert.Empty(service.ProductTerms("Name", "' OR 1=1 --"));
    }
    [Theory]
    [InlineData("MaterialCode")][InlineData("Unit")][InlineData("Name;DROP TABLE Product")]
    public void OnlyRequestedFieldsHaveSuggestions(string field) => Assert.Throws<BusinessException>(() => service.ProductTerms(field));
    [Fact] public void ResultLimitIsDeterministicAndNewTermsBecomeAvailable()
    {
        for (var i = 0; i < 55; i++) Add("飞驰" + i.ToString("D2"));
        Assert.Equal(50, service.ProductTerms("Name").Count);
        Assert.Equal(new[] { "飞驰00", "飞驰01", "飞驰02" }, service.ProductTerms("Name", limit: 3));
        Add("新款"); Assert.Equal(new[] { "新款" }, service.ProductTerms("Name", "新款"));
        Assert.Throws<BusinessException>(() => service.ProductTerms("Name", limit: 101));
    }
    [Fact] public void SuggestionsDoNotCreateProductsOrChangeStockAndAllowNewNames()
    {
        var first = Add("飞驰一代", "48V", "薄荷绿"); Add("飞驰二代", "60V", "天蓝");
        var before = service.Products();
        Assert.Equal(2, service.ProductTerms("Name", "飞驰").Count);
        Assert.Equal(before, service.Products());
        var newId = Add("飞驰", "新规格", "新颜色");
        Assert.NotEqual(first, newId); Assert.Equal(0, service.GetProduct(newId).Total);
        service.ValidateIntegrity();
    }
    [Fact] public void CancelledSearchDoesNotReturnResults()
    {
        using var source = new CancellationTokenSource(); source.Cancel();
        Assert.Throws<OperationCanceledException>(() => service.ProductTerms("Name", token: source.Token));
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
}

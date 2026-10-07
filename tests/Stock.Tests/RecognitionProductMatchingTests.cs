using Stock.Core;
using Xunit;

namespace Stock.Tests;

public sealed class RecognitionProductMatchingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "recognition-match-" + Guid.NewGuid().ToString("N"));
    private readonly StockService stock;
    public RecognitionProductMatchingTests() => stock = new(root);
    private long Add(string name, string spec = "48V", string color = "白", string code = "") =>
        stock.CreateProduct(ProductType.Vehicle, name, spec, color, code, 0, 0, Guid.NewGuid().ToString("N"));

    [Fact] public void FullIdentityUsesExactCodeAndEmptyIsNotAWildcard()
    {
        var blank = Add("飞驰"); var a = Add("飞驰", code: "A"); Add("飞驰", code: "A1"); Add("飞驰", color: "蓝", code: "A");
        Assert.Equal(blank, Assert.Single(stock.MatchRecognitionProducts(ProductType.Vehicle, "飞驰", "48V", "白", "")).Id);
        Assert.Equal(a, Assert.Single(stock.MatchRecognitionProducts(ProductType.Vehicle, "飞驰", "48V", "白", "A")).Id);
        Assert.Empty(stock.MatchRecognitionProducts(ProductType.Battery, "飞驰", "48V", "白", "A"));
        Assert.Empty(stock.MatchRecognitionProducts(ProductType.Vehicle, "飞", "48V", "白", "A"));
    }
    [Fact] public void TrimmedMultilineFieldsMatchBeyondFirstCandidatePage()
    {
        for (var i = 0; i < 110; i++) Add("飞驰", code: $"A{i:000}");
        var expected = Add("飞驰", "48V\n20Ah", "天蓝", "目标");
        Assert.Equal(expected, Assert.Single(stock.MatchRecognitionProducts(ProductType.Vehicle, " 飞驰 ", "48V\n20Ah ", " 天蓝", "目标 ")).Id);
    }
    [Fact] public void InactiveAndIncompleteProductsAreExcludedFromCloudMatches()
    {
        var id = Add("停用"); stock.UpdateProduct(id, "停用", "48V", false);
        stock.CreateProduct("旧款", "48V", "辆", 0, 0, Guid.NewGuid().ToString("N"));
        Assert.Empty(stock.MatchRecognitionProducts(ProductType.Vehicle, "停用", "48V", "白", ""));
        Assert.Empty(stock.MatchRecognitionProducts(ProductType.Unknown, "旧款", "48V", "", ""));
        Assert.Single(stock.MatchRecognitionProducts(ProductType.Unknown, "旧款", "48V", "", "", false));
    }
    [Fact] public void CancelledMatchingDoesNotChangeProductsOrStock()
    {
        Add("飞驰"); var before = stock.Products();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => stock.MatchRecognitionProducts(ProductType.Vehicle, "飞驰", "48V", "白", "", token: cancelled.Token));
        Assert.Equal(before, stock.Products()); stock.ValidateIntegrity();
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
}

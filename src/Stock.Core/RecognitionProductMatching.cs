namespace Stock.Core;

public sealed partial class StockService
{
    /// <summary>Find exact active identities without a candidate-page or substring-code limit.</summary>
    public IReadOnlyList<Product> MatchRecognitionProducts(ProductType type, string name, string spec, string color,
        string materialCode, bool completeIdentity = true, CancellationToken token = default)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            using var cn = Connect();
            var where = completeIdentity
                ? "p.Complete=1 AND p.Type=$type AND p.NameKey=$name AND p.SpecKey=$spec AND p.ColorKey=$color AND p.CodeKey=$code"
                : "p.NameKey IN ($name,$legacyName) AND p.SpecKey IN ($spec,$legacySpec)";
            using var cmd = Command(cn, null,
                $"SELECT {ProductColumns} FROM Product p JOIN StockBalance b ON b.ProductId=p.Id WHERE p.Active=1 AND {where} ORDER BY p.Id",
                ("$type", type.ToString()), ("$name", Rules.ExactIdentity(name)), ("$spec", Rules.ExactIdentity(spec)),
                ("$color", Rules.ExactIdentity(color)), ("$code", Rules.ExactIdentity(materialCode)),
                ("$legacyName", Rules.Identity(name)), ("$legacySpec", Rules.Identity(spec)));
            using var reader = cmd.ExecuteReader();
            var products = new List<Product>();
            while (reader.Read()) { token.ThrowIfCancellationRequested(); products.Add(ReadProduct(reader)); }
            return products;
        }
    }
}

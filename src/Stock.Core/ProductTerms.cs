namespace Stock.Core;

public sealed partial class StockService
{
    /// <summary>Existing field values for optional entry suggestions; never creates or associates a product.</summary>
    public IReadOnlyList<string> ProductTerms(string field, string search = "", int limit = 50, CancellationToken token = default)
    {
        if (field is not ("Name" or "Spec" or "Color") || limit is < 1 or > 100)
            throw new BusinessException("词条字段或数量无效。");
        token.ThrowIfCancellationRequested();
        using var cn = Connect();
        using var cmd = Command(cn, null, $"""
            WITH Terms AS (
                SELECT DISTINCT {field} AS Value FROM Product
                WHERE {field} IS NOT NULL AND length(trim({field})) > 0
            )
            SELECT Value FROM Terms WHERE instr(lower(Value), lower($text)) > 0
            ORDER BY CASE
                WHEN lower(Value) = lower($text) THEN 0
                WHEN instr(lower(Value), lower($text)) = 1 THEN 1
                ELSE 2 END, Value COLLATE NOCASE, Value
            LIMIT $limit
            """, ("$text", search.Trim()), ("$limit", limit));
        using var registration = token.Register(cmd.Cancel);
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            result.Add(reader.GetString(0));
        }
        token.ThrowIfCancellationRequested();
        return result;
    }
}

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Shared = Stock.Recognition;

namespace Stock.Core;

public sealed record RecognitionFeedbackRow(string RowId, int LineOrder, RecognizedRow Original, bool HumanConfirmed,
    IReadOnlyDictionary<string, string>? FieldSources = null);
public sealed record RecognitionFeedback(string Style, IReadOnlyList<RecognitionFeedbackRow> Rows, int Version = 1);
public sealed record CorrectionMemoryEntry(string DocumentId, string RowId, string Style, RecognizedRow Original,
    DocumentLine Final, bool HumanConfirmed, bool Enabled, string RecordedAt)
{
    public string Display => $"{Original.Name} / {Original.Spec} / {Original.Color} → {Final.Name} / {Final.Spec} / {Final.Color}；数量 {Final.Quantity}；{(HumanConfirmed ? "已人工确认" : "识别记录")}；{(Enabled ? "启用" : "停用")}";
}
public sealed record RecognitionMatch(IReadOnlyList<Product> Candidates, Product? Selected, string Source);

public sealed partial class StockService
{
    public string RecognitionMemoryPath => Path.Combine(DataDirectory, "recognition-memory.db");
    public string? MemoryNotice { get; private set; }
    private SqliteConnection MemoryConnection()
    {
        var cn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = RecognitionMemoryPath, Pooling = false }.ToString());
        try
        {
            cn.Open();
            if(Convert.ToInt32(Scalar(cn,null,"PRAGMA user_version"))>1)throw new IOException("核对记录来自较新版本，请升级软件。");
            Run(cn, null, """
                CREATE TABLE IF NOT EXISTS Correction(DocumentId TEXT NOT NULL, RowId TEXT NOT NULL, Style TEXT NOT NULL,
                    Original TEXT NOT NULL, Final TEXT NOT NULL, HumanConfirmed INTEGER NOT NULL, Enabled INTEGER NOT NULL DEFAULT 1,
                    RecordedAt TEXT NOT NULL, PRIMARY KEY(DocumentId,RowId));
                CREATE TABLE IF NOT EXISTS Setting(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
                PRAGMA user_version=1;
                """);
            return cn;
        }
        catch { cn.Dispose(); throw; }
    }
    public bool LearningEnabled
    {
        get { lock (gate) { using var cn = MemoryConnection(); return (string?)Scalar(cn, null, "SELECT Value FROM Setting WHERE Key='enabled'") != "false"; } }
        set { lock (gate) { using var cn = MemoryConnection(); Run(cn, null, "INSERT INTO Setting VALUES('enabled',$v) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value", ("$v", value ? "true" : "false")); } }
    }
    public void SetCorrectionEnabled(string documentId, string rowId, bool enabled)
    { lock (gate) { using var cn = MemoryConnection(); Run(cn, null, "UPDATE Correction SET Enabled=$e WHERE DocumentId=$d AND RowId=$r", ("$e", enabled ? 1 : 0), ("$d", documentId), ("$r", rowId)); } }

    // Called only after the stock transaction commits; its durable source is attachment metadata in stock.db.
    private void RememberCommittedDocument(string id)
    {
        try
        {
            FaultInjector?.Invoke("BeforeMemoryWrite");
            using var stock = Connect(); var document = GetDocument(stock, null, id);
            if (document.Kind != DocumentKind.Purchase || document.Status != RecordStatus.Valid || !LearningEnabled) return;
            using var memory = MemoryConnection(); using var tx = memory.BeginTransaction();
            using var cmd = Command(stock, null, "SELECT Metadata FROM Attachment WHERE DocumentId=$d", ("$d", id)); using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var metadata = reader.GetString(0); if (metadata.Length == 0) continue;
                using var json = JsonDocument.Parse(metadata);
                if (!json.RootElement.TryGetProperty("feedback", out var data)) continue;
                var feedback = data.Deserialize<RecognitionFeedback>(); if (feedback?.Version != 1) continue;
                foreach (var row in feedback.Rows)
                {
                    var final = document.Lines.SingleOrDefault(l => l.LineOrder == row.LineOrder); if (final is null) continue;
                    Run(memory, tx, """
                        INSERT INTO Correction(DocumentId,RowId,Style,Original,Final,HumanConfirmed,RecordedAt)
                        VALUES($d,$r,$s,$o,$f,$h,$at) ON CONFLICT(DocumentId,RowId) DO NOTHING
                        """, ("$d", id), ("$r", row.RowId), ("$s", feedback.Style), ("$o", JsonSerializer.Serialize(row.Original)),
                        ("$f", JsonSerializer.Serialize(final)), ("$h", row.HumanConfirmed ? 1 : 0), ("$at", document.OccurredAt));
                }
            }
            tx.Commit(); MemoryNotice = null;
        }
        catch (Exception)
        { MemoryNotice = "本次入库已成功。纠错记忆暂未保存，可在设置中点“补记已入库的修正”。"; }
    }
    public void RebuildRecognitionMemory()
    {
        lock (gate)
        {
            using var stock = Connect();
            using var cmd = Command(stock, null, """
                SELECT DISTINCT d.Id FROM Document d JOIN Attachment a ON a.DocumentId=d.Id
                WHERE d.Kind='Purchase' AND d.Status='Valid' AND a.Metadata LIKE '%"feedback"%'
                """); using var reader = cmd.ExecuteReader();
            var ids = new List<string>(); while (reader.Read()) ids.Add(reader.GetString(0));
            foreach (var id in ids) RememberCommittedDocument(id);
        }
    }
    public IReadOnlyList<CorrectionMemoryEntry> CorrectionMemory()
    {
        lock (gate)
        {
            using var cn = MemoryConnection(); using var cmd = Command(cn, null, "SELECT DocumentId,RowId,Style,Original,Final,HumanConfirmed,Enabled,RecordedAt FROM Correction ORDER BY RecordedAt DESC,RowId");
            using var reader = cmd.ExecuteReader(); var items = new List<CorrectionMemoryEntry>();
            while (reader.Read()) items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                JsonSerializer.Deserialize<RecognizedRow>(reader.GetString(3))!, JsonSerializer.Deserialize<DocumentLine>(reader.GetString(4))!,
                reader.GetInt64(5) != 0, reader.GetInt64(6) != 0, reader.GetString(7)));
            return items;
        }
    }
    private IReadOnlyList<CorrectionMemoryEntry> ValidCorrections()
    {
        using var cn = Connect();
        using var cmd = Command(cn, null, "SELECT Id FROM Document WHERE Status='Valid' AND Kind='Purchase'");
        using var reader = cmd.ExecuteReader(); var valid = new HashSet<string>(); while (reader.Read()) valid.Add(reader.GetString(0));
        var entries = CorrectionMemory().Where(e => e.Enabled && e.HumanConfirmed && valid.Contains(e.DocumentId) && (e.Original.Name.Length>0||e.Original.MaterialCode.Length>0)).ToList();
        return entries.Where(e =>
        {
            try
            {
                var p = ProductById(cn, null, e.Final.ProductId);
                return p.Active && p.Complete && p.Type == e.Final.Type && p.Name == e.Final.Name && p.Spec == e.Final.Spec &&
                    (p.Color ?? "") == (e.Final.Color ?? "") && (p.MaterialCode ?? "") == (e.Final.MaterialCode ?? "");
            }
            catch (BusinessException) { return false; }
        }).ToArray();
    }
    private static string OriginalIdentity(RecognizedRow row) => JsonSerializer.Serialize(new[]
        { row.Type.ToString(), Rules.ExactIdentity(row.Name), Rules.ExactIdentity(row.Spec), Rules.ExactIdentity(row.Color), Rules.ExactIdentity(row.MaterialCode) });
    public static string InvoiceStyle(RecognitionResult result) => JsonSerializer.Serialize(result.Rows.Select(r =>
        $"{r.SectionEvidence.Trim()}|{r.RawUnit.Trim()}|{r.QuantityColumn.Trim()}").Distinct().Order(StringComparer.Ordinal));

    public IReadOnlyList<Shared.CorrectionExample> RelevantCorrectionExamples(IEnumerable<string> terms, string? style = null)
    {
        lock (gate)
        {
            try
            {
                if (!LearningEnabled) return [];
                var search = terms.Select(Rules.ExactIdentity).Where(s => s.Length >= 2).Distinct().ToArray();
                if (search.Length == 0 && style is null) return [];
                return ValidCorrections().Where(e => style is null || e.Style == style)
                    .Where(e => search.Length == 0 || search.Any(t => e.Original.Name.Contains(t, StringComparison.Ordinal) || e.Final.Name.Contains(t, StringComparison.Ordinal) || e.Original.MaterialCode == t))
                    .DistinctBy(e => (OriginalIdentity(e.Original), e.Final.ProductId)).Take(3)
                    .Select(e => new Shared.CorrectionExample(e.Original.Name, e.Original.Spec, e.Original.Color,
                        e.Final.Name, e.Final.Spec, e.Final.Color ?? "", e.Final.MaterialCode ?? "", (Shared.RecognitionProductType)e.Final.Type)).ToArray();
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or JsonException) { return []; }
        }
    }
    public RecognitionMatch MatchRecognition(RecognizedRow row, string style, CancellationToken token = default)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            var exact = MatchRecognitionProducts(row.Type, row.Name, row.Spec, row.Color, row.MaterialCode, token: token);
            if (exact.Count == 1) return new(exact, exact[0], "货品资料");
            if (row.MaterialCode.Length > 0)
            {
                using var cn = Connect(); using var cmd = Command(cn, null, $"SELECT {ProductColumns} FROM Product p JOIN StockBalance b ON p.Id=b.ProductId WHERE p.Active=1 AND p.Complete=1 AND p.CodeKey=$c", ("$c", Rules.ExactIdentity(row.MaterialCode)));
                using var reader = cmd.ExecuteReader(); var coded = new List<Product>(); while (reader.Read()) coded.Add(ReadProduct(reader));
                bool Compatible(string recognized, string actual) => recognized.Length == 0 || Rules.ExactIdentity(recognized) == Rules.ExactIdentity(actual);
                if (coded.Count == 1 && (row.Type == ProductType.Unknown || row.Type == coded[0].Type) &&
                    Compatible(row.Name, coded[0].Name) && Compatible(row.Spec, coded[0].Spec) && Compatible(row.Color, coded[0].Color ?? ""))
                    return new(coded, coded[0], "货品编码");
            }
            try
            {
                if (LearningEnabled)
                {
                    var related = ValidCorrections().Where(e => e.Style == style && OriginalIdentity(e.Original) == OriginalIdentity(row)).ToArray();
                    var targets = related.Select(e => e.Final.ProductId).Distinct().ToArray();
                    if (targets.Length == 1)
                    {
                        var p = GetProduct(targets[0]);
                        return new([p], related.Select(e => e.DocumentId).Distinct().Count() >= 2 ? p : null, "以前的核对记录");
                    }
                    if (targets.Length > 1) return new(targets.Select(GetProduct).ToArray(), null, "以前的核对记录有不同选择，请选择货品");
                }
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or JsonException) { }
            var candidates = row.Name.Length == 0 ? new List<Product>() : ProductPage(search: row.Name).Items.Where(p => p.Complete && p.Active).Take(20).ToList();
            return new(candidates, null, "请选择货品");
        }
    }
    private void BackupRecognitionMemory(string destination)
    {
        if (!File.Exists(RecognitionMemoryPath)) return;
        using var source = MemoryConnection(); using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        target.Open(); source.BackupDatabase(target);
    }
}

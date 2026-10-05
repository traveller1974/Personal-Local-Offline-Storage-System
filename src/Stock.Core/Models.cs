using System.Globalization;
using System.Text;

namespace Stock.Core;

public sealed class BusinessException(string message) : Exception(message);
public interface IClock { DateTimeOffset Now { get; } DateOnly Today { get; } }
public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
    public DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
}

public enum DocumentKind { Opening, Purchase, Sale, Transfer, Void }
public enum RecordStatus { Valid, Voided }
public enum StatusFilter { Valid, Voided, All }
public enum DatePreset { Today, Yesterday, Last7Days, PreviousMonth, Last3Months, Last6Months, PreviousYear, Custom }

public sealed record Product(long Id, string Name, string Spec, string Unit, bool Active, long Warehouse, long Store)
{
    public long Total => Warehouse + Store;
    public string Display => $"{Name}  {Spec}（{Unit}）";
}
public sealed record LineInput(long ProductId, long Quantity);
public sealed record StockImpact(Product Product, long WarehouseDelta, long StoreDelta)
{
    public long WarehouseAfter => Product.Warehouse + WarehouseDelta;
    public long StoreAfter => Product.Store + StoreDelta;
    public long TotalAfter => WarehouseAfter + StoreAfter;
}
public sealed record DocumentLine(long ProductId, string Name, string Spec, string Unit, long Quantity, long WarehouseDelta, long StoreDelta);
public sealed record DocumentRecord(string Id, string Number, DocumentKind Kind, string Channel, RecordStatus Status,
    DateOnly BusinessDate, string OccurredAt, string? OriginalId, string? VoidId, string? VoidAt, string Reason,
    IReadOnlyList<DocumentLine> Lines, IReadOnlyList<string> Attachments);
public sealed record QueryFilter(DateOnly Start, DateOnly End, DocumentKind? Kind = null, string? Channel = null,
    string Search = "", StatusFilter Status = StatusFilter.Valid, bool Inventory = false);
public sealed record MaintenanceResult(DateOnly Cutoff, int DeletedDocuments, int DeletedPhotos);

public static class Rules
{
    public const long MaxQuantity = int.MaxValue;
    public static string Identity(string value) => value.Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant();
    public static string Clean(string value) => value.Normalize(NormalizationForm.FormKC).Trim();
    public static void Quantity(long value, bool zeroAllowed = false)
    {
        if (value < (zeroAllowed ? 0 : 1) || value > MaxQuantity) throw new BusinessException(zeroAllowed ? "数量必须是0到2147483647之间的整数。" : "数量必须是1到2147483647之间的整数。");
    }
    public static void Balance(long warehouse, long store)
    {
        if (warehouse < 0 || store < 0) throw new BusinessException("仓库或店面库存不足，操作未记账。请先调整库存分布或核对单据。");
        if (warehouse > MaxQuantity || store > MaxQuantity || warehouse + store > MaxQuantity) throw new BusinessException("库存合计超过允许上限，操作未记账。");
    }
    public static (DateOnly Start, DateOnly End) Dates(DatePreset preset, DateOnly today) => preset switch
    {
        DatePreset.Today => (today, today), DatePreset.Yesterday => (today.AddDays(-1), today.AddDays(-1)),
        DatePreset.Last7Days => (today.AddDays(-6), today),
        DatePreset.PreviousMonth => (new DateOnly(today.Year, today.Month, 1).AddMonths(-1), new DateOnly(today.Year, today.Month, 1).AddDays(-1)),
        DatePreset.Last3Months => (today.AddMonths(-3), today), DatePreset.Last6Months => (today.AddMonths(-6), today),
        DatePreset.PreviousYear => (new DateOnly(today.Year - 1, 1, 1), new DateOnly(today.Year - 1, 12, 31)),
        _ => (today, today)
    };
    public static string KindName(DocumentKind kind) => kind switch { DocumentKind.Opening => "期初", DocumentKind.Purchase => "进货", DocumentKind.Sale => "出货", DocumentKind.Transfer => "调拨", _ => "作废反向" };
    public static string DateText(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

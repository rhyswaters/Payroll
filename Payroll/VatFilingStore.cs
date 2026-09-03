using System.Text.Json;
using Payroll.Core;

namespace Payroll;

/// <summary>
/// Persists which VAT periods have actually been filed and paid, so --vat-return can detect a skipped
/// period instead of silently moving on to whatever's most recently completed - see YearToDateStore for
/// the equivalent problem on the payroll side.
/// </summary>
public sealed class VatFilingStore(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public List<VatFilingRecord> GetAll()
    {
        if (!File.Exists(filePath)) return [];
        return JsonSerializer.Deserialize<List<VatFilingRecord>>(File.ReadAllText(filePath)) ?? [];
    }

    public void MarkFiled(VatFilingRecord record)
    {
        var all = GetAll();
        all.RemoveAll(r => r.PeriodStart == record.PeriodStart);
        all.Add(record);
        File.WriteAllText(filePath, JsonSerializer.Serialize(all.OrderBy(r => r.PeriodStart), JsonOptions));
    }

    /// <summary>Completed periods between the earliest filing on record and (but not including)
    /// <paramref name="upTo"/> that have no filing recorded. Empty if there's no filing history yet at
    /// all - nothing to compare against, so nothing to flag.</summary>
    public IReadOnlyList<DateOnly> FindGaps(DateOnly upTo)
    {
        var all = GetAll();
        if (all.Count == 0) return [];

        var filedStarts = all.Select(r => r.PeriodStart).ToHashSet();
        var gaps = new List<DateOnly>();
        for (var cursor = all.Min(r => r.PeriodStart); cursor < upTo; cursor = cursor.AddMonths(2))
            if (!filedStarts.Contains(cursor))
                gaps.Add(cursor);
        return gaps;
    }
}

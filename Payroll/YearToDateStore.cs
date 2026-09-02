using System.Text.Json;
using Payroll.Core;

namespace Payroll;

/// <summary>
/// Persists running year-to-date totals locally, keyed by tax year - the job Revenue expects the
/// employer's own payroll software to do for an ongoing employment (see PayrollCalculator remarks).
/// </summary>
public sealed class YearToDateStore(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public YearToDateTotals Get(int taxYear)
    {
        if (!File.Exists(filePath)) return YearToDateTotals.Zero;
        var all = JsonSerializer.Deserialize<Dictionary<string, YearToDateTotals>>(File.ReadAllText(filePath)) ?? [];
        return all.GetValueOrDefault(taxYear.ToString(), YearToDateTotals.Zero);
    }

    public void Set(int taxYear, YearToDateTotals totals)
    {
        var all = File.Exists(filePath)
            ? JsonSerializer.Deserialize<Dictionary<string, YearToDateTotals>>(File.ReadAllText(filePath)) ?? []
            : [];
        all[taxYear.ToString()] = totals;
        File.WriteAllText(filePath, JsonSerializer.Serialize(all, JsonOptions));
    }
}

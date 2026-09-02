namespace Payroll.Core;

/// <summary>
/// A PRSI class's employee rate as it applies from a given date. Revenue's RPN does not carry PRSI
/// rates (unlike PAYE/USC bands) — payroll software is expected to know the statutory rate for the
/// class in use, and that rate changes on scheduled dates (e.g. Class S: 4.2% moving to 4.3% from
/// 1 October 2026), so it's kept here as an effective-dated table rather than a single constant.
/// </summary>
public sealed record PrsiRatePeriod(DateOnly EffectiveFrom, decimal EmployeeRatePercent, decimal EmployerRatePercent);

public sealed class PrsiSettings
{
    public required string PrsiClass { get; init; }
    public required IReadOnlyList<PrsiRatePeriod> RateHistory { get; init; }

    public PrsiRatePeriod RateFor(DateOnly payDate) =>
        RateHistory
            .Where(r => r.EffectiveFrom <= payDate)
            .OrderByDescending(r => r.EffectiveFrom)
            .FirstOrDefault()
        ?? throw new InvalidOperationException($"No PRSI rate defined for class {PrsiClass} effective on or before {payDate:yyyy-MM-dd}.");

    // Class S (proprietary directors / self-employed): employee-only contribution, no employer PRSI.
    // Rates confirmed against a real August 2026 BulletHQ payslip (€245.00 on €5,833.33 = 4.2%).
    public static PrsiSettings ClassS => new()
    {
        PrsiClass = "S",
        RateHistory =
        [
            new PrsiRatePeriod(new DateOnly(2025, 10, 1), 4.2m, 0m),
            new PrsiRatePeriod(new DateOnly(2026, 10, 1), 4.3m, 0m)
        ]
    };
}

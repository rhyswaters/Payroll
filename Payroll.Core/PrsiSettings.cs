namespace Payroll.Core;

/// <summary>
/// A PRSI class's employee rate as it applies from a given date. Revenue's RPN does not carry PRSI
/// rates (unlike PAYE/USC bands) — payroll software is expected to know the statutory rate for the
/// class in use, and that rate changes on scheduled dates (e.g. Class S: 4.2% moving to 4.35% from
/// 1 October 2026), so it's kept here as an effective-dated table rather than a single constant.
/// </summary>
/// <param name="EmployeeRatePercent">Null means a rate for this period is scheduled but not yet
/// confirmed - see <see cref="PrsiSettings.RateFor"/>, which refuses to guess and throws instead.</param>
public sealed record PrsiRatePeriod(DateOnly EffectiveFrom, decimal? EmployeeRatePercent, decimal EmployerRatePercent);

public sealed class PrsiSettings
{
    public required string PrsiClass { get; init; }
    public required IReadOnlyList<PrsiRatePeriod> RateHistory { get; init; }

    /// <summary>The most recent period on or before <paramref name="payDate"/>. Deliberately does not
    /// fall back to an earlier confirmed rate once a later, not-yet-confirmed period has started - a
    /// placeholder entry with a null rate (see <see cref="ClassS"/>) forces this to throw rather than
    /// silently assume the rate didn't change, so a forgotten check fails payroll instead of quietly
    /// undercharging PRSI.</summary>
    public PrsiRatePeriod RateFor(DateOnly payDate)
    {
        var period = RateHistory
            .Where(r => r.EffectiveFrom <= payDate)
            .OrderByDescending(r => r.EffectiveFrom)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"No PRSI rate defined for class {PrsiClass} effective on or before {payDate:yyyy-MM-dd}.");

        if (period.EmployeeRatePercent is null)
            throw new InvalidOperationException(
                $"PRSI rate for class {PrsiClass} effective {period.EffectiveFrom:yyyy-MM-dd} hasn't been confirmed yet. " +
                "Check https://www.gov.ie/en/department-of-social-protection/publications/prsi-class-s-rates/ (or that " +
                "year's Budget documents) and fill in the rate in PrsiSettings.cs before running payroll.");

        return period;
    }

    // Class S (proprietary directors / self-employed): employee-only contribution, no employer PRSI.
    // Part of a multi-year phased increase (Budget 2024, running 2024-2028 to fund the Social Insurance
    // Fund/state pension age) - each step takes effect 1 October, not the calendar-year boundary most
    // other tax changes use. 4.2% confirmed against a real August 2026 BulletHQ payslip (€245.00 on
    // €5,833.33). 4.35% from 1 October 2026 corroborated against multiple sources including the
    // published blended 2026 self-assessment rate (4.2375% = 4.2%*9/12 + 4.35%*3/12, matches exactly).
    // Check gov.ie/en/department-of-social-protection/publications/prsi-class-s-rates/ each Budget.
    public static PrsiSettings ClassS => new()
    {
        PrsiClass = "S",
        RateHistory =
        [
            new PrsiRatePeriod(new DateOnly(2025, 10, 1), 4.2m, 0m),
            new PrsiRatePeriod(new DateOnly(2026, 10, 1), 4.35m, 0m),
            // Legislated as +0.15% (Social Welfare (Miscellaneous Provisions) Act 2024) but not yet
            // independently confirmed for Class S specifically - verify before this date, then fill in.
            new PrsiRatePeriod(new DateOnly(2027, 10, 1), null, 0m),
            // Legislated as +0.2% - same caveat.
            new PrsiRatePeriod(new DateOnly(2028, 10, 1), null, 0m)
        ]
    };
}

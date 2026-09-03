namespace Payroll.Core;

/// <summary>
/// Ireland's standard bi-monthly VAT periods: Jan-Feb, Mar-Apr, May-Jun, Jul-Aug, Sep-Oct, Nov-Dec.
/// </summary>
public sealed record VatPeriod(DateOnly Start, DateOnly End)
{
    /// <summary>The most recently completed bi-monthly period as of the given date - e.g. on any day in
    /// September, this returns Jul 1 - Aug 31, never the still-in-progress Sep-Oct period.</summary>
    public static VatPeriod MostRecentlyCompleted(DateOnly today)
    {
        var currentPeriodStart = PeriodStart(today);
        var previousPeriodEnd = currentPeriodStart.AddDays(-1);
        var previousPeriodStart = PeriodStart(previousPeriodEnd);
        return new VatPeriod(previousPeriodStart, previousPeriodEnd);
    }

    /// <summary>The bi-monthly period containing the given date - e.g. any day in September returns
    /// Sep 1 - Oct 31, even though that period isn't over yet. Useful for an in-progress running total,
    /// not for anything that should only ever look at a closed period (like --vat-return).</summary>
    public static VatPeriod Containing(DateOnly date)
    {
        var start = PeriodStart(date);
        return new VatPeriod(start, start.AddMonths(2).AddDays(-1));
    }

    private static DateOnly PeriodStart(DateOnly date)
    {
        var startMonth = date.Month - ((date.Month - 1) % 2);
        return new DateOnly(date.Year, startMonth, 1);
    }
}

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

    private static DateOnly PeriodStart(DateOnly date)
    {
        var startMonth = date.Month - ((date.Month - 1) % 2);
        return new DateOnly(date.Year, startMonth, 1);
    }
}

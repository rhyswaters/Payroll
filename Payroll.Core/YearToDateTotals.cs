namespace Payroll.Core;

/// <summary>
/// Running cumulative totals for one employment in one tax year. Revenue's RPN only carries this kind
/// of figure over from a *previous ceased* employment (for recommencements) - for an ongoing employment
/// it's the employer's own payroll software that must track it, so this has to be persisted locally
/// between pay runs rather than read back from ROS.
///
/// PrsiDeductedToDate is informational only (PRSI isn't cumulative - see PayrollCalculator - so nothing
/// reads it back into a calculation), kept purely so a running total can be reported.
/// </summary>
public sealed record YearToDateTotals(
    decimal PayForIncomeTaxToDate,
    decimal IncomeTaxDeductedToDate,
    decimal PayForUscToDate,
    decimal UscDeductedToDate,
    decimal PrsiDeductedToDate = 0m)
{
    public static readonly YearToDateTotals Zero = new(0m, 0m, 0m, 0m, 0m);

    public YearToDateTotals Add(PayslipResult payslip) => new(
        PayForIncomeTaxToDate + payslip.PayForIncomeTax,
        IncomeTaxDeductedToDate + payslip.IncomeTax,
        PayForUscToDate + payslip.PayForUsc,
        UscDeductedToDate + payslip.Usc,
        PrsiDeductedToDate + payslip.EmployeePrsi);
}

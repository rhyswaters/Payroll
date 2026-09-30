namespace Payroll.Core;

/// <summary>
/// Running cumulative totals for one employment in one tax year. Revenue's RPN only carries this kind
/// of figure over from a *previous ceased* employment (for recommencements) - for an ongoing employment
/// it's the employer's own payroll software that must track it, so this has to be persisted locally
/// between pay runs rather than read back from ROS.
///
/// PrsiDeductedToDate is informational only (PRSI isn't cumulative - see PayrollCalculator - so nothing
/// reads it back into a calculation), kept purely so a running total can be reported.
///
/// LastPayDate is the pay date of the most recent payslip added, so a second run for the same month can
/// be caught before it's submitted and added on top. Null when unknown (totals entered via --seed-ytd, or
/// saved before this field existed).
/// </summary>
public sealed record YearToDateTotals(
    decimal PayForIncomeTaxToDate,
    decimal IncomeTaxDeductedToDate,
    decimal PayForUscToDate,
    decimal UscDeductedToDate,
    decimal PrsiDeductedToDate = 0m,
    DateOnly? LastPayDate = null)
{
    public static readonly YearToDateTotals Zero = new(0m, 0m, 0m, 0m, 0m);

    public YearToDateTotals Add(PayslipResult payslip) => new(
        PayForIncomeTaxToDate + payslip.PayForIncomeTax,
        IncomeTaxDeductedToDate + payslip.IncomeTax,
        PayForUscToDate + payslip.PayForUsc,
        UscDeductedToDate + payslip.Usc,
        PrsiDeductedToDate + payslip.EmployeePrsi,
        payslip.Inputs.PayDate);
}

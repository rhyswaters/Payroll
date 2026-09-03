namespace Payroll.Core;

/// <summary>
/// Calculates a single payslip using the same cumulative basis Revenue's RPN drives: income tax and
/// USC are worked out on year-to-date pay against year-to-date bands/credits, then netted against
/// what's already been deducted this year, so mid-year credit or band changes on the RPN self-correct.
/// PRSI is a flat per-period rate on gross pay (Revenue doesn't carry PRSI rates on the RPN).
///
/// Year-to-date pay/tax is taken from <paramref name="yearToDate"/>, not from the RPN's own to-date
/// fields - those are only populated by Revenue for a *previous ceased* employment (recommencements),
/// and stay zero for the life of an ongoing one. Tracking the real running total is the payroll
/// software's job, same as it was BulletHQ's.
/// </summary>
public static class PayrollCalculator
{
    public static PayslipResult Calculate(RpnDetails rpn, YearToDateTotals yearToDate, PrsiSettings prsi, PayrollInputs inputs)
    {
        if (inputs.EmployeeId != rpn.EmployeeId)
            throw new ArgumentException($"RPN is for employee {rpn.EmployeeId} but inputs are for {inputs.EmployeeId}.");

        // Notional pay (Benefits in Kind - medical insurance, a company car, etc.) inflates the
        // taxable/reckonable base for PAYE, USC and PRSI exactly like cash pay would, but - since it's
        // never actually paid to the employee - it must not add to net pay.
        var totalBenefitInKind = (inputs.BenefitsInKind ?? []).Sum(b => b.Amount);
        var taxableGrossPay = inputs.GrossPay + totalBenefitInKind;

        var payForIncomeTax = taxableGrossPay - inputs.EmployeePensionContribution;
        var incomeTax = CalculateCumulativePeriodDeduction(
            payForIncomeTax, yearToDate.PayForIncomeTaxToDate, yearToDate.IncomeTaxDeductedToDate,
            rpn.YearlyTaxCredits, rpn.TaxRates, inputs.PeriodNumber, inputs.PeriodsInYear);

        // USC is charged on full taxable gross pay - pension contributions give no USC relief.
        var payForUsc = taxableGrossPay;
        var usc = rpn.UscStatus.Equals("EXEMPT", StringComparison.OrdinalIgnoreCase)
            ? 0m
            : CalculateCumulativePeriodDeduction(
                payForUsc, yearToDate.PayForUscToDate, yearToDate.UscDeductedToDate,
                yearlyCredits: 0m, rpn.UscRates, inputs.PeriodNumber, inputs.PeriodsInYear);

        // PRSI is reckoned per period on taxable gross pay (no pension relief, no RPN band data involved).
        var payForEmployeePrsi = taxableGrossPay;
        var prsiRate = prsi.RateFor(inputs.PayDate);
        // RateFor already throws if the rate for this date isn't confirmed yet, so this is always non-null.
        var prsiRatePercent = prsiRate.EmployeeRatePercent!.Value;
        var employeePrsi = Round(payForEmployeePrsi * prsiRatePercent / 100m);

        var netPay = inputs.GrossPay - inputs.EmployeePensionContribution - incomeTax - usc - employeePrsi + inputs.EworkingAllowance;

        return new PayslipResult(
            inputs, rpn.RpnNumber,
            Round(payForIncomeTax), incomeTax,
            Round(payForUsc), usc,
            prsi.PrsiClass, prsiRatePercent, Round(payForEmployeePrsi), employeePrsi,
            Round(netPay));
    }

    private static decimal CalculateCumulativePeriodDeduction(
        decimal payThisPeriod, decimal payToDateBeforeThisPeriod, decimal deductedToDate,
        decimal yearlyCredits, IReadOnlyList<RateBand> bands, int periodNumber, int periodsInYear)
    {
        var cumulativePay = payToDateBeforeThisPeriod + payThisPeriod;
        var cumulativeGrossDue = TaxAcrossCumulativeBands(cumulativePay, bands, periodNumber, periodsInYear);
        var cumulativeCredits = yearlyCredits * periodNumber / periodsInYear;
        var cumulativeDue = Math.Max(0m, cumulativeGrossDue - cumulativeCredits);
        return Round(cumulativeDue - deductedToDate);
    }

    private static decimal TaxAcrossCumulativeBands(
        decimal cumulativePay, IReadOnlyList<RateBand> bands, int periodNumber, int periodsInYear)
    {
        decimal tax = 0m;
        decimal previousCumulativeCutOff = 0m;
        foreach (var band in bands.OrderBy(b => b.Index))
        {
            var cumulativeCutOff = band.YearlyCutOff.HasValue
                ? band.YearlyCutOff.Value * periodNumber / periodsInYear
                : decimal.MaxValue;

            var amountInBand = Math.Max(0m, Math.Min(cumulativePay, cumulativeCutOff) - previousCumulativeCutOff);
            tax += amountInBand * band.RatePercent / 100m;
            previousCumulativeCutOff = cumulativeCutOff;

            if (cumulativeCutOff >= cumulativePay) break;
        }
        return tax;
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

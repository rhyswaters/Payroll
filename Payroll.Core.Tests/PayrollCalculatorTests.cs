using Payroll.Core;
using Xunit;

namespace Payroll.Core.Tests;

public class PayrollCalculatorTests
{
    private static readonly EmployeeId Employee = new("1234567T", "1");

    private static RpnDetails FreshYearRpn() => new(
        RpnNumber: "1",
        EmployeeId: Employee,
        RpnIssueDate: new DateOnly(2026, 1, 1),
        EffectiveDate: new DateOnly(2026, 1, 1),
        EndDate: new DateOnly(2026, 12, 31),
        IncomeTaxCalculationBasis: IncomeTaxCalculationBasis.Cumulative,
        YearlyTaxCredits: 3600m,
        TaxRates: [new RateBand(1, 20m, 40000m), new RateBand(2, 40m, null)],
        PayForIncomeTaxToDate: 0m,
        IncomeTaxDeductedToDate: 0m,
        UscStatus: "ORDINARY",
        UscRates: [new RateBand(1, 0.5m, 12012m), new RateBand(2, 2m, 27382m), new RateBand(3, 8m, null)],
        PayForUscToDate: 0m,
        UscDeductedToDate: 0m);

    private static readonly YearToDateTotals FreshYear = YearToDateTotals.Zero;

    private static readonly PrsiSettings PrsiClassS = PrsiSettings.ClassS;

    [Fact]
    public void FirstMonth_ChargesTaxOnFullMonthlyCreditAndBand()
    {
        var rpn = FreshYearRpn();
        var inputs = PayrollInputs.MonthlyFor("LI-1", Employee, "Rhys", "Waters", new DateOnly(2026, 1, 31), 5000m, 500m);

        var result = PayrollCalculator.Calculate(rpn, FreshYear, PrsiClassS, inputs);

        // payForIncomeTax = 4500; cumulative cut-off for month 1 = 40000/12 = 3333.33; credit = 3600/12 = 300
        // tax = 3333.3333*0.20 + (4500-3333.3333)*0.40 - 300 = 833.33
        Assert.Equal(833.33m, result.IncomeTax);
        Assert.Equal(4500m, result.PayForIncomeTax);
        Assert.Equal(5000m, result.PayForUsc);
        Assert.Equal(5000m, result.PayForEmployeePrsi);
        Assert.Equal(210.00m, result.EmployeePrsi); // 4.2% of 5000
        Assert.Equal(5000m - 500m - result.IncomeTax - result.Usc - result.EmployeePrsi, result.NetPay);
    }

    [Fact]
    public void ReportsCumulativeCreditsAndCutOff_FromCurrentRpnApportionedToDate()
    {
        // Figures from a real August payslip produced by the previous payroll software.
        var rpn = FreshYearRpn() with
        {
            YearlyTaxCredits = 3822.60m,
            TaxRates = [new RateBand(1, 20m, 43113m), new RateBand(2, 40m, null)]
        };
        var inputs = PayrollInputs.MonthlyFor("LI-8", Employee, "Rhys", "Waters", new DateOnly(2026, 8, 30), 5000m, 500m);

        var result = PayrollCalculator.Calculate(rpn, FreshYear, PrsiClassS, inputs);

        Assert.Equal(2548.40m, result.CumulativeTaxCredits);
        Assert.Equal(28742.00m, result.CumulativeStandardRateCutOff);
        Assert.Equal(IncomeTaxCalculationBasis.Cumulative, result.TaxBasis);
    }

    [Fact]
    public void LevelSalaryAcrossFullYear_MatchesSingleAnnualCalculation_ToTheCent()
    {
        var rpn = FreshYearRpn();
        var yearToDate = YearToDateTotals.Zero;
        decimal totalTax = 0m, totalUsc = 0m;

        for (var month = 1; month <= 12; month++)
        {
            var inputs = PayrollInputs.MonthlyFor("LI", Employee, "Rhys", "Waters", new DateOnly(2026, month, 1), 5000m, 500m);

            var result = PayrollCalculator.Calculate(rpn, yearToDate, PrsiClassS, inputs);

            yearToDate = yearToDate.Add(result);
            totalTax += result.IncomeTax;
            totalUsc += result.Usc;
        }

        // A single once-off annual calculation on the full year's pay should land within a cent of
        // twelve cumulative monthly calculations for a level salary - that's the whole point of the
        // cumulative basis (it self-corrects every period).
        var annualTax = AnnualTax(60000m - 6000m, rpn.YearlyTaxCredits, rpn.TaxRates);
        var annualUsc = AnnualTax(60000m, 0m, rpn.UscRates);

        Assert.True(Math.Abs(totalTax - annualTax) <= 0.02m, $"totalTax={totalTax} annualTax={annualTax}");
        Assert.True(Math.Abs(totalUsc - annualUsc) <= 0.02m, $"totalUsc={totalUsc} annualUsc={annualUsc}");
    }

    [Fact]
    public void BenefitInKind_InflatesTaxableBaseButNotNetPay()
    {
        var rpn = FreshYearRpn();
        var withoutBik = PayrollCalculator.Calculate(rpn, FreshYear, PrsiClassS,
            PayrollInputs.MonthlyFor("LI", Employee, "Rhys", "Waters", new DateOnly(2026, 1, 31), 5000m, 500m));
        var withBik = PayrollCalculator.Calculate(rpn, FreshYear, PrsiClassS,
            PayrollInputs.MonthlyFor("LI", Employee, "Rhys", "Waters", new DateOnly(2026, 1, 31), 5000m, 500m,
                eworkingAllowance: 0m, benefitsInKind: [new BenefitInKindLine("Health Insurance (BIK)", 200m, BikCategory.MedicalInsurance)]));

        Assert.Equal(5200m, withBik.TaxableGrossPay);
        Assert.Equal(5000m, withBik.GrossPay);
        Assert.Equal(200m, withBik.TotalBenefitInKind);
        Assert.Equal(200m, withBik.MedicalInsuranceBenefitInKind);
        // BIK adds 200 to the base taxed at 40% (already past the standard cut-off this period) and to USC/PRSI.
        Assert.Equal(withoutBik.IncomeTax + 80.00m, withBik.IncomeTax); // 200 * 40%
        Assert.True(withBik.Usc > withoutBik.Usc);
        Assert.True(withBik.EmployeePrsi > withoutBik.EmployeePrsi);
        // None of that extra tax comes with extra cash - net pay must fall by exactly the extra deductions.
        var extraDeductions = (withBik.IncomeTax - withoutBik.IncomeTax) + (withBik.Usc - withoutBik.Usc) + (withBik.EmployeePrsi - withoutBik.EmployeePrsi);
        Assert.Equal(withoutBik.NetPay - extraDeductions, withBik.NetPay);
    }

    [Fact]
    public void GeneralCategoryBenefit_CountsTowardTotalButNotMedicalInsurance()
    {
        var rpn = FreshYearRpn();
        var result = PayrollCalculator.Calculate(rpn, FreshYear, PrsiClassS,
            PayrollInputs.MonthlyFor("LI", Employee, "Rhys", "Waters", new DateOnly(2026, 1, 31), 5000m, 500m,
                eworkingAllowance: 0m, benefitsInKind: [
                    new BenefitInKindLine("Company Car (BIK)", 150m, BikCategory.General),
                    new BenefitInKindLine("Health Insurance (BIK)", 200m, BikCategory.MedicalInsurance)
                ]));

        Assert.Equal(350m, result.TotalBenefitInKind);
        Assert.Equal(200m, result.MedicalInsuranceBenefitInKind);
    }

    private static decimal AnnualTax(decimal taxablePay, decimal yearlyCredits, IReadOnlyList<RateBand> bands)
    {
        decimal tax = 0m, previousCutOff = 0m;
        foreach (var band in bands.OrderBy(b => b.Index))
        {
            var cutOff = band.YearlyCutOff ?? decimal.MaxValue;
            var amountInBand = Math.Max(0m, Math.Min(taxablePay, cutOff) - previousCutOff);
            tax += amountInBand * band.RatePercent / 100m;
            previousCutOff = cutOff;
            if (cutOff >= taxablePay) break;
        }
        return Math.Max(0m, tax - yearlyCredits);
    }

    [Fact]
    public void EworkingAllowance_AffectsNeitherTaxableFiguresNorNetPay()
    {
        var payDate = new DateOnly(2026, 9, 28);
        var without = PayrollCalculator.Calculate(FreshYearRpn(), FreshYear, PrsiClassS,
            PayrollInputs.MonthlyFor("LI-1", Employee, "Rhys", "Waters", payDate, 5833.33m, 1458.33m));
        var with = PayrollCalculator.Calculate(FreshYearRpn(), FreshYear, PrsiClassS,
            PayrollInputs.MonthlyFor("LI-1", Employee, "Rhys", "Waters", payDate, 5833.33m, 1458.33m, eworkingAllowance: 64m));

        Assert.Equal(without.PayForIncomeTax, with.PayForIncomeTax);
        Assert.Equal(without.PayForUsc, with.PayForUsc);
        Assert.Equal(without.IncomeTax, with.IncomeTax);
        Assert.Equal(without.Usc, with.Usc);
        Assert.Equal(without.EmployeePrsi, with.EmployeePrsi);
        Assert.Equal(without.NetPay, with.NetPay);
        Assert.Equal(64m, with.EworkingAllowance);
    }

    [Fact]
    public void YearToDateAdd_RecordsThePayslipsPayDate_AndOlderSavedTotalsLoadWithoutOne()
    {
        var payDate = new DateOnly(2026, 9, 28);
        var result = PayrollCalculator.Calculate(FreshYearRpn(), FreshYear, PrsiClassS,
            PayrollInputs.MonthlyFor("LI-1", Employee, "Rhys", "Waters", payDate, 5000m, 500m));

        Assert.Equal(payDate, FreshYear.Add(result).LastPayDate);

        // year-to-date.json files written before LastPayDate existed must still load.
        var saved = System.Text.Json.JsonSerializer.Deserialize<YearToDateTotals>(
            """{"PayForIncomeTaxToDate":1,"IncomeTaxDeductedToDate":2,"PayForUscToDate":3,"UscDeductedToDate":4,"PrsiDeductedToDate":5}""")!;
        Assert.Null(saved.LastPayDate);
        Assert.Equal(1m, saved.PayForIncomeTaxToDate);
    }

    [Theory]
    // €500/week threshold = €2,166.67/month on a 52-week year.
    [InlineData(2166.66, "S0")]
    [InlineData(2166.67, "S1")]
    [InlineData(5833.33, "S1")]
    public void ClassS_ReportsSubclassFromWeeklyEquivalentPay(decimal gross, string expectedClass)
    {
        var inputs = PayrollInputs.MonthlyFor("LI-1", Employee, "Rhys", "Waters", new DateOnly(2026, 9, 28), gross, 0m);

        var result = PayrollCalculator.Calculate(FreshYearRpn(), FreshYear, PrsiClassS, inputs);

        Assert.Equal(expectedClass, result.PrsiClass);
    }
}

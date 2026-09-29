namespace Payroll.Core;

public sealed record PayslipResult(
    PayrollInputs Inputs,
    string RpnNumber,
    decimal PayForIncomeTax,
    decimal IncomeTax,
    decimal PayForUsc,
    decimal Usc,
    string PrsiClass,
    decimal PrsiRatePercent,
    decimal PayForEmployeePrsi,
    decimal EmployeePrsi,
    decimal NetPay,
    IncomeTaxCalculationBasis TaxBasis,
    /// <summary>The RPN's yearly tax credits apportioned to this period on the cumulative basis - what
    /// this payslip's PAYE was actually worked out against.</summary>
    decimal CumulativeTaxCredits,
    /// <summary>The RPN's yearly standard rate cut-off point (top of the 20% band) apportioned to this
    /// period on the cumulative basis.</summary>
    decimal CumulativeStandardRateCutOff
)
{
    public decimal GrossPay => Inputs.GrossPay;
    public decimal EmployeePensionContribution => Inputs.EmployeePensionContribution;
    public decimal EworkingAllowance => Inputs.EworkingAllowance;
    public IReadOnlyList<BenefitInKindLine> BenefitsInKind => Inputs.BenefitsInKind ?? [];

    public decimal TotalBenefitInKind => BenefitsInKind.Sum(b => b.Amount);
    public decimal MedicalInsuranceBenefitInKind => BenefitsInKind.Where(b => b.Category == BikCategory.MedicalInsurance).Sum(b => b.Amount);

    /// <summary>Gross pay including notional pay (BIK), before pension - what ROS calls "Gross Pay".</summary>
    public decimal TaxableGrossPay => GrossPay + TotalBenefitInKind;

    public decimal TotalDeductions => IncomeTax + Usc + EmployeePrsi + EmployeePensionContribution;
}

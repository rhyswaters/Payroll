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
    decimal NetPay
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

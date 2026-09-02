namespace Payroll.Core;

/// <summary>
/// The tax-affecting contents of a single employee's current Revenue Payroll Notification (RPN),
/// as returned by ROS's Lookup RPN web service. Field names mirror the ROS JSON schema so mapping
/// straight from the API response stays a 1:1 exercise.
/// </summary>
public sealed record RpnDetails(
    string RpnNumber,
    EmployeeId EmployeeId,
    DateOnly RpnIssueDate,
    DateOnly EffectiveDate,
    DateOnly? EndDate,
    IncomeTaxCalculationBasis IncomeTaxCalculationBasis,
    decimal YearlyTaxCredits,
    IReadOnlyList<RateBand> TaxRates,
    decimal PayForIncomeTaxToDate,
    decimal IncomeTaxDeductedToDate,
    string UscStatus,
    IReadOnlyList<RateBand> UscRates,
    decimal PayForUscToDate,
    decimal UscDeductedToDate
);

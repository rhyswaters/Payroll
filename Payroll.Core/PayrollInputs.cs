namespace Payroll.Core;

public sealed record PayrollInputs(
    string LineItemId,
    EmployeeId EmployeeId,
    string FirstName,
    string FamilyName,
    DateOnly PayDate,
    int PeriodNumber,
    int PeriodsInYear,
    decimal GrossPay,
    decimal EmployeePensionContribution,
    /// <summary>A tax-free reimbursement (e.g. the Revenue remote-working daily allowance) paid alongside
    /// salary. Excluded from PAYE/USC/PRSI and from the ROS payroll submission's taxable pay - it's added
    /// straight to net pay and recorded as its own Manager.io earnings line. Note: since 2024 this kind of
    /// payment has its own separate Revenue reporting obligation (Enhanced Reporting Requirements), which
    /// this calculator does not yet submit.
    decimal EworkingAllowance = 0m,
    /// <summary>Any Benefits in Kind for this period (e.g. employer-paid medical insurance, a company
    /// car). See <see cref="BenefitInKindLine"/> - adding a new kind of benefit is a config/data change
    /// (a new line here, a matching Manager.io account, a matching appsettings.json key), not a code
    /// change, as long as it fits the two ROS categories in <see cref="BikCategory"/>. Null and empty
    /// both mean "none this period".
    IReadOnlyList<BenefitInKindLine>? BenefitsInKind = null
)
{
    /// <summary>Defaults PeriodNumber to the pay date's calendar month, matching the common case of
    /// monthly payroll aligned to the (calendar-year) Irish tax year.</summary>
    public static PayrollInputs MonthlyFor(
        string lineItemId, EmployeeId employeeId, string firstName, string familyName,
        DateOnly payDate, decimal grossPay, decimal employeePensionContribution,
        decimal eworkingAllowance = 0m, IReadOnlyList<BenefitInKindLine>? benefitsInKind = null) =>
        new(lineItemId, employeeId, firstName, familyName, payDate, payDate.Month, 12,
            grossPay, employeePensionContribution, eworkingAllowance, benefitsInKind);
}

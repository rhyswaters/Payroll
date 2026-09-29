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
    /// salary. Excluded from PAYE/USC/PRSI, from the ROS payroll submission, from net pay and from the
    /// Manager.io payslip - it's paid as its own separate Manager.io payment and reported to Revenue via
    /// the Enhanced Reporting Requirements (ERR) submission instead.
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

namespace Payroll.Core;

/// <summary>
/// Determines which ROS field a benefit reports through. Revenue's Payroll Submission schema has a
/// dedicated field only for medical insurance (grossMedicalInsurance, used to cross-check the
/// employee's personal relief credit claim); every other non-cash benefit (a company car, subsidised
/// accommodation, a preferential loan...) is explicitly the generic "taxableBenefits" bucket - see
/// Revenue's PSR Data Items, item 47. Categories that need their OWN field entirely (share-based
/// remuneration, pension contributions) are not benefits in this sense and aren't modelled here.
/// </summary>
public enum BikCategory
{
    General,
    MedicalInsurance
}

/// <summary>
/// A single Benefit in Kind for one payslip: notional pay that inflates the PAYE/USC/PRSI taxable base
/// exactly like cash would, but is never actually paid to the employee, so it never adds to net pay.
/// </summary>
public sealed record BenefitInKindLine(string Description, decimal Amount, BikCategory Category);

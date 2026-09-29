namespace Payroll.ManagerIo;

public sealed class ManagerIoOptions
{
    public required string BaseUrl { get; init; }

    /// <summary>Sent as the X-API-KEY header. Set via `dotnet user-secrets set "ManagerIo:ApiKey" "..."`.</summary>
    public required string ApiKey { get; init; }

    public required string EmployeeKey { get; init; }
    public required string BankAccountKey { get; init; }

    /// <summary>The control account a salary payment's line is posted against (the employee clearing account Manager uses to settle payslips).</summary>
    public required string PaymentClearingAccountKey { get; init; }

    public required string PensionDeductionItemKey { get; init; }
    public required string PayeDeductionItemKey { get; init; }
    public required string UscDeductionItemKey { get; init; }
    public required string PrsiDeductionItemKey { get; init; }

    /// <summary>Maps a Benefit in Kind's Description (e.g. "Health Insurance (BIK)") to the Manager.io
    /// account/deduction item key that offsets its earnings line so it doesn't affect net pay. Adding a
    /// new kind of benefit is just adding an entry here (plus creating the matching Manager.io account)
    /// - no code change. A benefit whose Description isn't a key here throws rather than silently
    /// skipping the offsetting deduction.</summary>
    public IReadOnlyDictionary<string, string> BenefitInKindDeductionItemKeys { get; init; } =
        new Dictionary<string, string>();

    /// <summary>The expense account the tax-free e-working allowance is paid against, as its own payment
    /// separate from salary. Required only when a payslip has an e-working allowance.</summary>
    public string? EworkingAllowanceAccountKey { get; init; }

    /// <summary>Payslip number custom fields that record running year-to-date totals (after this
    /// payslip) - what an accountant reconciles payslips against. Optional; not set means none are filled.</summary>
    public PayslipYtdCustomFieldKeys? PayslipYtdCustomFieldKeys { get; init; }

    /// <summary>Payslip custom fields shown in the payslip's header (PPSN, PRSI class, tax basis and the
    /// cumulative credits/cut-off PAYE was calculated against). Optional; not set means none are filled.</summary>
    public PayslipHeaderCustomFieldKeys? PayslipHeaderCustomFieldKeys { get; init; }

    /// <summary>The system "VAT Payable" control account - only required for VAT return reconciliation.</summary>
    public string? VatPayableAccountKey { get; init; }

    /// <summary>A small P&amp;L account absorbing the rounding difference between the true accrued VAT
    /// liability and the whole-euro amount ROS actually calculates as owed.</summary>
    public string? VatRoundingAdjustmentAccountKey { get; init; }

    /// <summary>The payee/contact name used on payments made to Revenue (e.g. VAT settlements). Used to
    /// tell a liability-clearing payment apart from a genuine purchase, since both post an ordinary
    /// Payment line against "VAT Payable" and look identical otherwise. Defaults to "Revenue".</summary>
    public string RevenuePayeeName { get; init; } = "Revenue";
}

/// <summary>Keys of the Manager.io payslip number custom fields, named after the field each one fills
/// (Manager.io's own display names are in brackets).</summary>
public sealed class PayslipYtdCustomFieldKeys
{
    /// <summary>"Gross Pay Less Pension Deductions YTD" - pay for income tax to date.</summary>
    public string? PayForIncomeTax { get; set; }

    /// <summary>"Gross Pay YTD" - pay for USC to date (salary + benefits in kind, no pension relief).</summary>
    public string? PayForUsc { get; set; }

    /// <summary>"PAYE deducted YTD".</summary>
    public string? IncomeTax { get; set; }

    /// <summary>"USC deducted YTD".</summary>
    public string? Usc { get; set; }

    /// <summary>"PRSI deducted YTD".</summary>
    public string? Prsi { get; set; }
}

/// <summary>Keys of the Manager.io payslip header custom fields. Ppsn, PrsiClass and TaxBasis are text
/// fields; CumulativeTaxCredits and CumulativeCutOffPoint are number fields.</summary>
public sealed class PayslipHeaderCustomFieldKeys
{
    /// <summary>"PPSN".</summary>
    public string? Ppsn { get; set; }

    /// <summary>"PRSI Class" - the reported subclass, e.g. S1.</summary>
    public string? PrsiClass { get; set; }

    /// <summary>"Tax Basis" - Cumulative or Week 1, from the RPN.</summary>
    public string? TaxBasis { get; set; }

    /// <summary>"Cumulative Tax Credits".</summary>
    public string? CumulativeTaxCredits { get; set; }

    /// <summary>"Cumulative Cut-off Point" - standard rate cut-off point to date.</summary>
    public string? CumulativeCutOffPoint { get; set; }
}

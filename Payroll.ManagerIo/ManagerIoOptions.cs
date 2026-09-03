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

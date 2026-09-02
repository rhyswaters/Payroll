namespace Payroll.ManagerIo;

public sealed record VatLedgerLine(DateOnly Date, decimal Amount, string Source);

/// <summary>
/// VAT-on-sales and VAT-on-purchases for a period, pulled directly from the "VAT Payable" account's
/// real ledger lines (Receipts = sales, Payments = purchases) rather than recomputed from raw invoice
/// amounts - this sidesteps ever needing to know whether Manager.io's amounts are tax-inclusive or
/// exclusive, since Manager already resolved that before posting to the account.
/// </summary>
public sealed record VatFigures(
    decimal SalesVat,
    decimal PurchasesVat,
    IReadOnlyList<VatLedgerLine> SalesLines,
    IReadOnlyList<VatLedgerLine> PurchaseLines,
    /// <summary>Lines found on "VAT Payable" from credit notes, debit notes, expense claims, or journal
    /// entries in the period - these aren't included in SalesVat/PurchasesVat because their direction
    /// (sales-like vs purchase-like) isn't verified, unlike the common Receipt/Payment case. Non-empty
    /// means the totals above are probably incomplete and need manual review.</summary>
    IReadOnlyList<VatLedgerLine> UnexpectedLines);

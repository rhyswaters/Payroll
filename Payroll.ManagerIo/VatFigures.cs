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
    IReadOnlyList<VatLedgerLine> UnexpectedLines,
    /// <summary>A payment to Revenue itself (payee/contact "Revenue") that happens to touch "VAT Payable" -
    /// this settles a *prior* period's liability, it isn't input VAT on a purchase, so it's excluded from
    /// PurchasesVat even though Manager.io records it as an ordinary Payment line against the same account.
    /// Shown here for visibility rather than silently dropped.</summary>
    IReadOnlyList<VatLedgerLine> SettlementLines);

/// <summary>The "VAT Payable" account's actual running balance as of a date, summed from every
/// Receipt/Payment line ever posted to it (no period, no classification of *why* a payment was made -
/// unlike <see cref="VatFigures"/>, a payment settling a prior period's liability doesn't need telling
/// apart from a purchase here, because either way it genuinely reduces the balance). Matches what
/// Manager.io's own "Liabilities" summary would show. Positive = owed to Revenue, negative = Revenue owes you.</summary>
public sealed record VatPayableBalance(decimal Balance, IReadOnlyList<VatLedgerLine> UnexpectedLines);

namespace Payroll.Core;

/// <summary>A record that a VAT3 return for this period was actually filed and paid - lets --vat-return
/// detect a skipped period (one that completed but was never filed) instead of silently moving on to
/// whatever's most recently completed.</summary>
public sealed record VatFilingRecord(DateOnly PeriodStart, DateOnly PeriodEnd, DateOnly FiledOn, int RoundedSalesVat, int RoundedPurchasesVat);

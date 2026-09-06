namespace Payroll.ManagerIo;

/// <summary>One payment recorded as a business expense - a Manager.io Payment with a genuine
/// supplier/contact set (see <see cref="ManagerIoClient.GetExpensesReportAsync"/> for why that's what
/// tells an expense apart from payroll's own Salary payment or a VAT settlement to Revenue). Subtotal and
/// Vat are split by reading which of the payment's lines post to the configured VAT Payable account, the
/// same technique <see cref="VatFigures"/> uses for the VAT3 return.</summary>
public sealed record ExpenseLine(
    DateOnly IssueDate,
    string Supplier,
    decimal Total,
    decimal Subtotal,
    decimal Vat,
    decimal AmountPaid,
    string Description);

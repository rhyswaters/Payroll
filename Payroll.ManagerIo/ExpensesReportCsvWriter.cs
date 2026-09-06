using System.Text;

namespace Payroll.ManagerIo;

/// <summary>Builds the annual expenses CSV handed to the accountant for year-end accounts - same column
/// shape as the export BulletHQ used to (half-)produce, minus "Paid By" (no longer relevant now that
/// personal expenses aren't claimed through the business - the e-working allowance covers that via
/// payroll instead) and with "Description" read straight from each payment's own memo instead of being
/// left blank for manual entry afterward.</summary>
public static class ExpensesReportCsvWriter
{
    public static string Build(IReadOnlyList<ExpenseLine> expenses)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Issue Date,Supplier,Total,Subtotal,VAT,Amount Paid,Description,Notes");
        foreach (var e in expenses.OrderBy(e => e.IssueDate))
        {
            sb.AppendLine(string.Join(",",
                e.IssueDate.ToString("dd/MM/yyyy"),
                Escape(e.Supplier),
                e.Total.ToString("0.00"),
                e.Subtotal.ToString("0.00"),
                e.Vat.ToString("0.00"),
                e.AmountPaid.ToString("0.00"),
                Escape(e.Description),
                "" // Notes - nothing in Manager.io maps to this; left blank for manual annotation.
            ));
        }
        return sb.ToString();
    }

    private static string Escape(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}

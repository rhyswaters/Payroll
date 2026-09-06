using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Payroll.Core;
using Payroll.ManagerIo.Dto;

namespace Payroll.ManagerIo;

public sealed class ManagerIoClient : IDisposable
{
    private readonly ManagerIoOptions _options;
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ManagerIoClient(ManagerIoOptions options)
    {
        _options = options;
        _http = new HttpClient { BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/api2/") };
        _http.DefaultRequestHeaders.Add("X-API-KEY", options.ApiKey);
    }

    /// <summary>Creates a payslip recording the tax breakdown for a payslip already submitted to ROS.
    /// Field names/casing match Manager.io's own (inconsistent) API, copied from a live example.</summary>
    public async Task<string> CreatePayslipAsync(PayslipResult payslip, CancellationToken ct = default)
    {
        var earnings = new List<EarningsLineDto>
        {
            new() { Description = "Base Salary", UnitPrice = payslip.GrossPay }
        };
        if (payslip.EworkingAllowance > 0m)
            earnings.Add(new EarningsLineDto { Description = "e-working Allowance", UnitPrice = payslip.EworkingAllowance });

        var deductions = new List<DeductionLineDto>
        {
            new() { Item = _options.PensionDeductionItemKey, Description = "Pension", DeductionAmount = payslip.EmployeePensionContribution },
            new() { Item = _options.PayeDeductionItemKey, Description = "PAYE", DeductionAmount = payslip.IncomeTax },
            new() { Item = _options.UscDeductionItemKey, Description = "USC", DeductionAmount = payslip.Usc },
            new() { Item = _options.PrsiDeductionItemKey, Description = "PRSI", DeductionAmount = payslip.EmployeePrsi }
        };

        // Each Benefit in Kind is recorded as an earnings line (it's notional pay, so it belongs in the
        // taxable gross) offset by an equal non-cash deduction, so it doesn't change net pay - only the
        // real cash salary does. Adding a new kind of benefit is a config change (a new entry in
        // ManagerIo:BenefitInKindDeductionItemKeys, keyed by this exact Description) plus creating the
        // matching Manager.io account - not a code change.
        foreach (var benefit in payslip.BenefitsInKind)
        {
            if (!_options.BenefitInKindDeductionItemKeys.TryGetValue(benefit.Description, out var itemKey))
                throw new ManagerIoClientException(
                    $"This payslip has a Benefit in Kind '{benefit.Description}' but ManagerIo:BenefitInKindDeductionItemKeys " +
                    $"has no entry for it. Create a matching account in Manager.io and add its key under that exact description.");

            earnings.Add(new EarningsLineDto { Description = benefit.Description, UnitPrice = benefit.Amount });
            deductions.Add(new DeductionLineDto { Item = itemKey, Description = benefit.Description, DeductionAmount = benefit.Amount });
        }

        var body = new PayslipFormDto
        {
            Date = ToManagerIoDate(payslip.Inputs.PayDate),
            Employee = _options.EmployeeKey,
            Earnings = earnings,
            Deductions = deductions
        };

        using var response = await _http.PostAsJsonAsync("payslip-form", body, JsonOptions, ct);
        return await ExtractCreatedKey(response, ct);
    }

    public async Task<string> CreatePaymentAsync(DateOnly payDate, decimal amount, string description, CancellationToken ct = default)
    {
        var body = new PaymentFormDto
        {
            Date = ToManagerIoDate(payDate),
            PaidFrom = _options.BankAccountKey,
            Description = description,
            Lines = [new PaymentLineDto { Account = _options.PaymentClearingAccountKey, Employee = _options.EmployeeKey, Amount = amount }]
        };

        using var response = await _http.PostAsJsonAsync("payment-form", body, JsonOptions, ct);
        return await ExtractCreatedKey(response, ct);
    }

    private const string VatPayableAccountName = "VAT Payable";

    /// <summary>Pulls VAT-on-sales and VAT-on-purchases for a period directly from the "VAT Payable"
    /// account's real ledger lines (Receipts = sales, Payments = purchases). Also checks credit notes,
    /// debit notes, expense claims, and journal entries for the same account/period and reports them
    /// separately as <see cref="VatFigures.UnexpectedLines"/> rather than guessing their direction. A
    /// Payment line that belongs to a payment made to Revenue itself (settling a prior period's
    /// liability, not a purchase) is excluded from PurchasesVat and reported as
    /// <see cref="VatFigures.SettlementLines"/> instead - see <see cref="FetchVatSettlementLines"/>.</summary>
    public async Task<VatFigures> GetVatFiguresAsync(DateOnly start, DateOnly end, CancellationToken ct = default)
    {
        bool InPeriod(VatLedgerLine l) => l.Date >= start && l.Date <= end;

        var salesLines = (await FetchVatPayableLines("receipt-lines", "receiptLines", "Receipt", ct)).Where(InPeriod).ToList();

        var settlementLines = await FetchVatSettlementLines(ct);
        var settlementDatesAndAmounts = settlementLines.Select(l => (l.Date, l.Amount)).ToHashSet();
        var purchaseLines = (await FetchVatPayableLines("payment-lines", "paymentLines", "Payment", ct))
            .Where(l => !settlementDatesAndAmounts.Contains((l.Date, l.Amount)))
            .Where(InPeriod)
            .ToList();
        var settlementLinesInPeriod = settlementLines.Where(InPeriod).ToList();

        var unexpectedLines = new List<VatLedgerLine>();
        foreach (var (endpoint, rootProperty, source) in new[]
                 {
                     ("credit-note-lines", "creditNoteLines", "Credit Note"),
                     ("debit-note-lines", "debitNoteLines", "Debit Note"),
                     ("expense-claim-lines", "expenseClaimLines", "Expense Claim"),
                     ("journal-entry-lines", "journalEntryLines", "Journal Entry")
                 })
            unexpectedLines.AddRange((await FetchVatPayableLines(endpoint, rootProperty, source, ct)).Where(InPeriod));

        return new VatFigures(
            salesLines.Sum(l => l.Amount), purchaseLines.Sum(l => l.Amount),
            salesLines, purchaseLines, unexpectedLines, settlementLinesInPeriod);
    }

    /// <summary>The account's real running balance as of a date - see <see cref="VatPayableBalance"/> for
    /// why this doesn't need the settlement-payment exclusion <see cref="GetVatFiguresAsync"/> does.</summary>
    public async Task<VatPayableBalance> GetVatPayableBalanceAsync(DateOnly asOf, CancellationToken ct = default)
    {
        bool UpTo(VatLedgerLine l) => l.Date <= asOf;

        var salesLines = (await FetchVatPayableLines("receipt-lines", "receiptLines", "Receipt", ct)).Where(UpTo).ToList();
        var purchaseLines = (await FetchVatPayableLines("payment-lines", "paymentLines", "Payment", ct)).Where(UpTo).ToList();

        var unexpectedLines = new List<VatLedgerLine>();
        foreach (var (endpoint, rootProperty, source) in new[]
                 {
                     ("credit-note-lines", "creditNoteLines", "Credit Note"),
                     ("debit-note-lines", "debitNoteLines", "Debit Note"),
                     ("expense-claim-lines", "expenseClaimLines", "Expense Claim"),
                     ("journal-entry-lines", "journalEntryLines", "Journal Entry")
                 })
            unexpectedLines.AddRange((await FetchVatPayableLines(endpoint, rootProperty, source, ct)).Where(UpTo));

        return new VatPayableBalance(salesLines.Sum(l => l.Amount) - purchaseLines.Sum(l => l.Amount), unexpectedLines);
    }

    private static readonly Regex TaxRatePercentInName = new(@"(\d+(?:\.\d+)?)\s*%", RegexOptions.Compiled);

    /// <summary>Pulls every genuine business-expense Payment in a period for the accountant's year-end
    /// CSV - see <see cref="ExpensesReportCsvWriter"/>. Payroll's own Salary payment and a VAT settlement
    /// to Revenue both get excluded, neither is a business expense (Salary is reported via the payroll
    /// submission, VAT settlements via the VAT3 return) - see <see cref="ReadExpenseFinancials"/> for how
    /// Salary is told apart (its "payee" isn't reliable - observed null on some, the employee's own name
    /// on others). Subtotal and Vat are backed out of each remaining payment's own lines: a line's Amount
    /// is VAT-inclusive, and if it carries a TaxCode, that code's live rate (read from Manager.io's own
    /// tax-codes list, not hardcoded here) says how much of it is VAT. A future rate change only affects
    /// payments recorded against a new tax code, so this stays correct for old payments as long as
    /// Manager.io tax codes are never edited in place to change their rate.</summary>
    public async Task<List<ExpenseLine>> GetExpensesReportAsync(DateOnly start, DateOnly end, CancellationToken ct = default)
    {
        var taxRatePercentByCode = await FetchTaxCodeRates(ct);

        var results = new List<ExpenseLine>();
        var skip = 0;
        const int pageSize = 200;

        while (true)
        {
            using var response = await _http.GetAsync($"payments?pageSize={pageSize}&skip={skip}", ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            var payments = doc.RootElement.GetProperty("payments");

            var count = 0;
            foreach (var payment in payments.EnumerateArray())
            {
                count++;

                if (!payment.TryGetProperty("payee", out var payeeProp) || payeeProp.GetString() is not { Length: > 0 } payee) continue;
                if (string.Equals(payee, _options.RevenuePayeeName, StringComparison.OrdinalIgnoreCase)) continue;

                if (!payment.TryGetProperty("date", out var dateProp) || dateProp.GetString() is not { } dateString) continue;
                var date = DateOnly.Parse(dateString);
                if (date < start || date > end) continue;

                if (!payment.TryGetProperty("key", out var keyProp)) continue;
                var description = payment.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? "" : "";

                var financials = await ReadExpenseFinancials(keyProp.GetString()!, taxRatePercentByCode, ct);
                if (financials is not { } f) continue;
                results.Add(new ExpenseLine(date, payee, f.Subtotal + f.Vat, f.Subtotal, f.Vat, f.Subtotal + f.Vat, description));
            }

            if (count < pageSize) break;
            skip += pageSize;
        }

        return results.OrderBy(r => r.IssueDate).ToList();
    }

    /// <summary>Reads one payment's Subtotal/Vat split, or null if it's actually payroll's Salary payment
    /// in disguise. Every Salary payment this app has ever created posts its one line straight to the
    /// payroll clearing account (<see cref="ManagerIoOptions.PaymentClearingAccountKey"/>) against an
    /// Employee - unlike the "payee" list field (observed inconsistent: null on some Salary payments, the
    /// employee's own name on others, so not safe to filter on alone), which account a line posts to is
    /// structural and can't vary, so it's the one reliable signal to exclude it here.</summary>
    private async Task<(decimal Subtotal, decimal Vat)?> ReadExpenseFinancials(
        string paymentKey, IReadOnlyDictionary<string, decimal> taxRatePercentByCode, CancellationToken ct)
    {
        using var formResponse = await _http.GetAsync($"payment-form/{paymentKey}", ct);
        formResponse.EnsureSuccessStatusCode();
        using var formDoc = JsonDocument.Parse(await formResponse.Content.ReadAsStreamAsync(ct));

        var vat = 0m;
        var gross = 0m;
        foreach (var line in formDoc.RootElement.GetProperty("Lines").EnumerateArray())
        {
            if (line.TryGetProperty("Account", out var accountProp) && accountProp.GetString() == _options.PaymentClearingAccountKey)
                return null;

            if (!line.TryGetProperty("Amount", out var amountProp)) continue;
            var lineAmount = amountProp.GetDecimal();
            gross += lineAmount;

            if (!line.TryGetProperty("TaxCode", out var taxCodeProp) || taxCodeProp.GetString() is not { } taxCode) continue;

            if (!taxRatePercentByCode.TryGetValue(taxCode, out var ratePercent))
                throw new ManagerIoClientException(
                    $"Payment {paymentKey} uses tax code {taxCode}, which isn't in Manager.io's current tax-codes list - it may have been deleted.");

            // lineAmount is VAT-inclusive, so the VAT portion has to be backed out rather than added on top.
            vat += lineAmount - lineAmount / (1 + ratePercent / 100m);
        }

        var roundedVat = Round(vat);
        return (Round(gross) - roundedVat, roundedVat);
    }

    /// <summary>Maps each tax code's key to its rate, parsed from its name (e.g. "VAT 23%" -> 23) since
    /// Manager.io's API exposes no separate numeric rate field. Deliberately fetched fresh on every call
    /// rather than cached anywhere in this app - Manager.io's tax-codes list is the one source of truth
    /// for current rates, the same way PrsiSettings is for PRSI, so a Budget rate change takes effect the
    /// moment a new code for it exists there, with no code change here.</summary>
    private async Task<Dictionary<string, decimal>> FetchTaxCodeRates(CancellationToken ct)
    {
        using var response = await _http.GetAsync("tax-codes?pageSize=200", ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));

        var rates = new Dictionary<string, decimal>();
        foreach (var code in doc.RootElement.GetProperty("taxCodes").EnumerateArray())
        {
            if (!code.TryGetProperty("key", out var keyProp) || !code.TryGetProperty("name", out var nameProp)) continue;
            var name = nameProp.GetString() ?? "";
            var match = TaxRatePercentInName.Match(name);
            if (!match.Success)
                throw new ManagerIoClientException(
                    $"Manager.io tax code '{name}' doesn't have a rate this app can parse (expected e.g. \"VAT 23%\") - rename it or fix ManagerIoClient.FetchTaxCodeRates.");
            rates[keyProp.GetString()!] = decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return rates;
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Finds "VAT Payable" lines belonging to payments made to Revenue (payee/contact matching
    /// <see cref="ManagerIoOptions.RevenuePayeeName"/>) - across all time, not just the period being asked
    /// about, since GetVatFiguresAsync needs the full set to exclude matching lines from PurchasesVat.
    /// The plain "payments" list doesn't expose per-account line amounts, only the payment total, so each
    /// matching payment needs a follow-up "payment-form/{key}" call to read its actual line breakdown.</summary>
    private async Task<List<VatLedgerLine>> FetchVatSettlementLines(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.VatPayableAccountKey)) return [];

        var settlementLines = new List<VatLedgerLine>();
        var skip = 0;
        const int pageSize = 200;

        while (true)
        {
            using var response = await _http.GetAsync($"payments?pageSize={pageSize}&skip={skip}", ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            var payments = doc.RootElement.GetProperty("payments");

            var count = 0;
            foreach (var payment in payments.EnumerateArray())
            {
                count++;
                if (!payment.TryGetProperty("payee", out var payeeProp) ||
                    !string.Equals(payeeProp.GetString(), _options.RevenuePayeeName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!payment.TryGetProperty("key", out var keyProp)) continue;

                using var formResponse = await _http.GetAsync($"payment-form/{keyProp.GetString()}", ct);
                formResponse.EnsureSuccessStatusCode();
                using var formDoc = JsonDocument.Parse(await formResponse.Content.ReadAsStreamAsync(ct));
                if (!formDoc.RootElement.TryGetProperty("Date", out var dateProp) || dateProp.GetString() is not { } dateString) continue;
                var date = DateOnly.Parse(dateString.Split('T')[0]);

                foreach (var line in formDoc.RootElement.GetProperty("Lines").EnumerateArray())
                {
                    if (!line.TryGetProperty("Account", out var accountProp) || accountProp.GetString() != _options.VatPayableAccountKey) continue;
                    if (!line.TryGetProperty("Amount", out var amountProp)) continue;
                    settlementLines.Add(new VatLedgerLine(date, amountProp.GetDecimal(), "VAT Settlement Payment"));
                }
            }

            if (count < pageSize) break;
            skip += pageSize;
        }

        return settlementLines;
    }

    /// <summary>Settles the VAT Payable account for a period: one line clears the exact unrounded amount
    /// accrued in the books, a second absorbs the (usually tiny, occasionally negative) difference from
    /// ROS's whole-euro rounding against a dedicated rounding-adjustment account, so VAT Payable lands
    /// on exactly zero afterward instead of drifting by a few cents every period.</summary>
    public async Task<string> CreateVatReconciliationPaymentAsync(
        DateOnly paymentDate, VatReturn vatReturn, string description, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.VatPayableAccountKey) || string.IsNullOrWhiteSpace(_options.VatRoundingAdjustmentAccountKey))
            throw new ManagerIoClientException("ManagerIo:VatPayableAccountKey and ManagerIo:VatRoundingAdjustmentAccountKey must both be configured.");

        var lines = new List<PaymentLineDto> { new() { Account = _options.VatPayableAccountKey, Amount = vatReturn.UnroundedNetLiability } };
        if (vatReturn.RoundingAdjustment != 0m)
            lines.Add(new PaymentLineDto { Account = _options.VatRoundingAdjustmentAccountKey, Amount = vatReturn.RoundingAdjustment });

        var body = new PaymentFormDto
        {
            Date = ToManagerIoDate(paymentDate), PaidFrom = _options.BankAccountKey,
            Contact = _options.RevenuePayeeName, Description = description, Lines = lines
        };
        using var response = await _http.PostAsJsonAsync("payment-form", body, JsonOptions, ct);
        return await ExtractCreatedKey(response, ct);
    }

    private async Task<List<VatLedgerLine>> FetchVatPayableLines(string endpoint, string rootProperty, string source, CancellationToken ct)
    {
        var results = new List<VatLedgerLine>();
        var skip = 0;
        const int pageSize = 200;

        while (true)
        {
            using var response = await _http.GetAsync($"{endpoint}?pageSize={pageSize}&skip={skip}", ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            var lines = doc.RootElement.GetProperty(rootProperty);

            var count = 0;
            foreach (var line in lines.EnumerateArray())
            {
                count++;
                if (!line.TryGetProperty("account", out var accountProp) || accountProp.GetString() != VatPayableAccountName) continue;
                if (!line.TryGetProperty("date", out var dateProp) || string.IsNullOrEmpty(dateProp.GetString())) continue;
                if (!line.TryGetProperty("amount", out var amountProp) || !amountProp.TryGetProperty("value", out var valueProp)) continue;

                results.Add(new VatLedgerLine(DateOnly.Parse(dateProp.GetString()!), valueProp.GetDecimal(), source));
            }

            if (count < pageSize) break;
            skip += pageSize;
        }

        return results;
    }

    // DateOnly.ToString rejects any custom format string containing a time separator (":"), even as
    // literal text - so the "T00:00:00" suffix Manager.io expects has to be appended separately.
    private static string ToManagerIoDate(DateOnly date) => date.ToString("yyyy-MM-dd") + "T00:00:00";

    private static async Task<string> ExtractCreatedKey(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new ManagerIoClientException($"Manager.io returned {(int)response.StatusCode} {response.StatusCode}: {errorBody}");
        }

        // The API only documents "201 Resource created" with no response schema. Observed behaviour:
        // it echoes the full created record back with an "id" field (also duplicated as "Key").
        var dto = await response.Content.ReadFromJsonAsync<FormCreatedResponseDto>(cancellationToken: ct);
        if (dto?.Id is { } id) return id;

        if (response.Headers.Location is { } location)
            return location.Segments[^1].TrimEnd('/');

        throw new ManagerIoClientException(
            "Manager.io created the record but returned no id or Location header - check manually in Manager.io.");
    }

    public void Dispose() => _http.Dispose();
}

public sealed class ManagerIoClientException(string message) : Exception(message);

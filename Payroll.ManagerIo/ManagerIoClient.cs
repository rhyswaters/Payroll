using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    /// separately as <see cref="VatFigures.UnexpectedLines"/> rather than guessing their direction.</summary>
    public async Task<VatFigures> GetVatFiguresAsync(DateOnly start, DateOnly end, CancellationToken ct = default)
    {
        bool InPeriod(VatLedgerLine l) => l.Date >= start && l.Date <= end;

        var salesLines = (await FetchVatPayableLines("receipt-lines", "receiptLines", "Receipt", ct)).Where(InPeriod).ToList();
        var purchaseLines = (await FetchVatPayableLines("payment-lines", "paymentLines", "Payment", ct)).Where(InPeriod).ToList();

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
            salesLines, purchaseLines, unexpectedLines);
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

        var body = new PaymentFormDto { Date = ToManagerIoDate(paymentDate), PaidFrom = _options.BankAccountKey, Description = description, Lines = lines };
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

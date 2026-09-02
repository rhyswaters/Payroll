using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Payroll.Core;
using Payroll.Ros.Dto;

namespace Payroll.Ros;

public sealed class RosClient : IDisposable
{
    private readonly RosOptions _options;
    private readonly HttpClient _http;
    public RosOptions Options => _options;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public RosClient(RosOptions options)
    {
        _options = options;
        var certificate = RosCertificateLoader.Load(options.P12Path, options.P12PlainPassword);
        var signingHandler = new RosHttpSignatureHandler(certificate, new HttpClientHandler());
        _http = new HttpClient(signingHandler) { BaseAddress = options.BaseAddress };
    }

    public async Task<RpnDetails> LookupRpnAsync(int taxYear, EmployeeId employeeId, CancellationToken ct = default)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["softwareUsed"] = _options.SoftwareUsed;
        query["softwareVersion"] = _options.SoftwareVersion;
        if (_options.AgentTain is not null) query["agentTain"] = _options.AgentTain;
        query.Add("employeeIDs", $"{employeeId.EmployeePpsn}-{employeeId.EmploymentId}");

        var path = $"paye-employers/v1/rest/rpn/{_options.EmployerRegistrationNumber}/{taxYear}?{query}";
        using var response = await _http.GetAsync(path, ct);
        await EnsureSuccess(response, ct);

        var dto = await response.Content.ReadFromJsonAsync<RpnLookupResponseDto>(JsonOptions, ct)
            ?? throw new RosClientException("ROS returned an empty RPN lookup response.");

        var item = dto.Rpns.FirstOrDefault(r =>
            r.EmployeeId.EmployeePpsn == employeeId.EmployeePpsn && r.EmployeeId.EmploymentId == employeeId.EmploymentId)
            ?? throw new RosClientException($"No RPN found for {employeeId} in tax year {taxYear}. A new employment may need to be registered on ROS first.");

        return MapToRpnDetails(item);
    }

    /// <summary>Lists every RPN ROS holds for this employer in a tax year, unfiltered - useful for
    /// finding the actual employmentID Revenue assigned when it doesn't match what you expected.</summary>
    public async Task<IReadOnlyList<RpnDetails>> ListAllRpnsAsync(int taxYear, CancellationToken ct = default)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["softwareUsed"] = _options.SoftwareUsed;
        query["softwareVersion"] = _options.SoftwareVersion;
        if (_options.AgentTain is not null) query["agentTain"] = _options.AgentTain;

        var path = $"paye-employers/v1/rest/rpn/{_options.EmployerRegistrationNumber}/{taxYear}?{query}";
        using var response = await _http.GetAsync(path, ct);
        await EnsureSuccess(response, ct);

        var dto = await response.Content.ReadFromJsonAsync<RpnLookupResponseDto>(JsonOptions, ct)
            ?? throw new RosClientException("ROS returned an empty RPN lookup response.");

        return dto.Rpns.Select(MapToRpnDetails).ToList();
    }

    public async Task<string> CreatePayrollSubmissionAsync(
        string taxYear, string payrollRunReference, string submissionId, PayslipResult payslip, CancellationToken ct = default)
    {
        var body = new PayrollSubmissionRequestDto
        {
            Payslips = [MapToPayslipSubmission(payslip)]
        };

        var path = $"paye-employers/v1/rest/payroll/{_options.EmployerRegistrationNumber}/{taxYear}/{payrollRunReference}/{submissionId}" +
                   $"?softwareUsed={Uri.EscapeDataString(_options.SoftwareUsed)}&softwareVersion={Uri.EscapeDataString(_options.SoftwareVersion)}";

        using var response = await _http.PostAsJsonAsync(path, body, JsonOptions, ct);
        await EnsureSuccess(response, ct);

        var dto = await response.Content.ReadFromJsonAsync<AcknowledgementResponseDto>(JsonOptions, ct)
            ?? throw new RosClientException("ROS returned an empty payroll submission response.");

        if (dto.Errors is { Count: > 0 })
            throw new RosClientException("Payroll submission was rejected: " +
                string.Join("; ", dto.Errors.Select(e => $"{e.Code} {e.Path}: {e.Description}")));

        return dto.AcknowledgementId ?? throw new RosClientException("ROS acknowledged the submission but returned no acknowledgement ID.");
    }

    /// <summary>Reports a tax-free remote/e-working daily allowance payment via the Enhanced Reporting
    /// Requirements (ERR) submission web service - a separate obligation from the payroll submission,
    /// required since 2024 for this category of payment.</summary>
    public async Task<string> SubmitRemoteWorkingAllowanceAsync(
        string taxYear, string errRunReference, string submissionId,
        EmployeeId employeeId, string firstName, string familyName, ErrAddress address, DateOnly dateOfBirth,
        string employerReference, int numberOfDays, DateOnly paymentDate, decimal amount, CancellationToken ct = default)
    {
        var body = new ErrSubmissionRequestDto
        {
            ExpensesBenefits =
            [
                new ExpenseBenefitItemDto
                {
                    LineItemId = $"ERR-{paymentDate:yyyy-MM}",
                    EmployeeId = new EmployeeIdDto { EmployeePpsn = employeeId.EmployeePpsn, EmploymentId = employeeId.EmploymentId },
                    EmployerReference = employerReference,
                    Name = new NameDto { FirstName = firstName, FamilyName = familyName },
                    Address = new AddressDto
                    {
                        AddressLines = address.AddressLines.Select(l => new AddressLineDto { AddressLine = l }).ToList(),
                        County = address.County,
                        Eircode = address.Eircode,
                        CountryCode = address.CountryCode
                    },
                    DateOfBirth = dateOfBirth.ToString("yyyy-MM-dd"),
                    Category = "REMOTE_WORKING_DAILY_ALLOWANCE",
                    NumberOfDays = numberOfDays,
                    PaymentDate = paymentDate.ToString("yyyy-MM-dd"),
                    Amount = amount
                }
            ]
        };

        var path = $"paye-employers/v1/rest/enhanced_reporting/{_options.EmployerRegistrationNumber}/{taxYear}/{errRunReference}/{submissionId}" +
                   $"?softwareUsed={Uri.EscapeDataString(_options.SoftwareUsed)}&softwareVersion={Uri.EscapeDataString(_options.SoftwareVersion)}";

        using var response = await _http.PostAsJsonAsync(path, body, JsonOptions, ct);
        await EnsureSuccess(response, ct);

        var dto = await response.Content.ReadFromJsonAsync<AcknowledgementResponseDto>(JsonOptions, ct)
            ?? throw new RosClientException("ROS returned an empty ERR submission response.");

        if (dto.Errors is { Count: > 0 })
            throw new RosClientException("ERR submission was rejected: " +
                string.Join("; ", dto.Errors.Select(e => $"{e.Code} {e.Path}: {e.Description}")));

        return dto.AcknowledgementId ?? throw new RosClientException("ROS acknowledged the ERR submission but returned no acknowledgement ID.");
    }

    public async Task<CheckPayrollSubmissionResponseDto> CheckPayrollSubmissionAsync(
        string taxYear, string payrollRunReference, string submissionId, CancellationToken ct = default)
    {
        var path = $"paye-employers/v1/rest/payroll/{_options.EmployerRegistrationNumber}/{taxYear}/{payrollRunReference}/{submissionId}" +
                   $"?softwareUsed={Uri.EscapeDataString(_options.SoftwareUsed)}&softwareVersion={Uri.EscapeDataString(_options.SoftwareVersion)}";
        using var response = await _http.GetAsync(path, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<CheckPayrollSubmissionResponseDto>(JsonOptions, ct)
            ?? throw new RosClientException("ROS returned an empty check-submission response.");
    }

    private static RpnDetails MapToRpnDetails(RpnItemDto item) => new(
        item.RpnNumber,
        new EmployeeId(item.EmployeeId.EmployeePpsn, item.EmployeeId.EmploymentId),
        DateOnly.Parse(item.RpnIssueDate),
        DateOnly.Parse(item.EffectiveDate),
        item.EndDate is null ? null : DateOnly.Parse(item.EndDate),
        item.IncomeTaxCalculationBasis.Equals("WEEK1", StringComparison.OrdinalIgnoreCase) ||
        item.IncomeTaxCalculationBasis.Equals("WEEK 1", StringComparison.OrdinalIgnoreCase)
            ? IncomeTaxCalculationBasis.Week1 : IncomeTaxCalculationBasis.Cumulative,
        item.YearlyTaxCredits,
        item.TaxRates.Select(b => new RateBand(b.Index, b.TaxRatePercent ?? 0m, b.YearlyRateCutOff)).ToList(),
        item.PayForIncomeTaxToDate,
        item.IncomeTaxDeductedToDate,
        item.UscStatus,
        item.UscRates.Select(b => new RateBand(b.Index, b.UscRatePercent ?? 0m, b.YearlyUscRateCutOff)).ToList(),
        item.PayForUscToDate,
        item.UscDeductedToDate);

    private static PayslipSubmissionDto MapToPayslipSubmission(PayslipResult p) => new()
    {
        LineItemId = p.Inputs.LineItemId,
        EmployeeId = new EmployeeIdDto { EmployeePpsn = p.Inputs.EmployeeId.EmployeePpsn, EmploymentId = p.Inputs.EmployeeId.EmploymentId },
        Name = new NameDto { FirstName = p.Inputs.FirstName, FamilyName = p.Inputs.FamilyName },
        PayFrequency = "MONTHLY",
        NumberOfPayPeriods = 12,
        RpnNumber = p.RpnNumber,
        ExclusionOrder = false,
        PayDate = p.Inputs.PayDate.ToString("yyyy-MM-dd"),
        GrossPay = p.TaxableGrossPay,
        PayForIncomeTax = p.PayForIncomeTax,
        IncomeTaxPaid = p.IncomeTax,
        PayForEmployeePrsi = p.PayForEmployeePrsi,
        PayForEmployerPrsi = 0m,
        PrsiExempt = false,
        PrsiClassDetails = [new PrsiClassDetailDto { PrsiClass = p.PrsiClass, InsurableWeeks = 4 }],
        EmployeePrsiPaid = p.EmployeePrsi,
        EmployerPrsiPaid = 0m,
        PayForUsc = p.PayForUsc,
        UscStatus = "ORDINARY",
        UscPaid = p.Usc,
        LptDeducted = 0m,
        TaxableBenefits = p.TotalBenefitInKind > 0m ? p.TotalBenefitInKind : null,
        GrossMedicalInsurance = p.MedicalInsuranceBenefitInKind > 0m ? p.MedicalInsuranceBenefitInKind : null
    };

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new RosClientException($"ROS returned {(int)response.StatusCode} {response.StatusCode}: {body}");
    }

    public void Dispose() => _http.Dispose();
}

public sealed class RosClientException(string message) : Exception(message);

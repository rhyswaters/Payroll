using System.Text.Json.Serialization;

namespace Payroll.Ros.Dto;

public sealed class PrsiClassDetailDto
{
    [JsonPropertyName("prsiClass")] public required string PrsiClass { get; init; }
    [JsonPropertyName("insurableWeeks")] public required int InsurableWeeks { get; init; }
}

public sealed class PayslipSubmissionDto
{
    [JsonPropertyName("lineItemID")] public required string LineItemId { get; init; }
    [JsonPropertyName("employeeID")] public required EmployeeIdDto EmployeeId { get; init; }
    [JsonPropertyName("name")] public required NameDto Name { get; init; }
    [JsonPropertyName("payFrequency")] public required string PayFrequency { get; init; }
    [JsonPropertyName("numberOfPayPeriods")] public required int NumberOfPayPeriods { get; init; }
    [JsonPropertyName("rpnNumber")] public required string RpnNumber { get; init; }
    [JsonPropertyName("exclusionOrder")] public bool ExclusionOrder { get; init; }
    [JsonPropertyName("payDate")] public required string PayDate { get; init; }
    [JsonPropertyName("grossPay")] public decimal GrossPay { get; init; }
    [JsonPropertyName("payForIncomeTax")] public decimal PayForIncomeTax { get; init; }
    [JsonPropertyName("incomeTaxPaid")] public decimal IncomeTaxPaid { get; init; }
    [JsonPropertyName("payForEmployeePRSI")] public decimal PayForEmployeePrsi { get; init; }
    [JsonPropertyName("payForEmployerPRSI")] public decimal PayForEmployerPrsi { get; init; }
    [JsonPropertyName("prsiExempt")] public bool PrsiExempt { get; init; }
    [JsonPropertyName("prsiClassDetails")] public required List<PrsiClassDetailDto> PrsiClassDetails { get; init; }
    [JsonPropertyName("employeePRSIPaid")] public decimal EmployeePrsiPaid { get; init; }
    [JsonPropertyName("employerPRSIPaid")] public decimal EmployerPrsiPaid { get; init; }
    [JsonPropertyName("payForUSC")] public decimal PayForUsc { get; init; }
    [JsonPropertyName("uscStatus")] public required string UscStatus { get; init; }
    [JsonPropertyName("uscPaid")] public decimal UscPaid { get; init; }
    [JsonPropertyName("lptDeducted")] public decimal LptDeducted { get; init; }
    [JsonPropertyName("taxableBenefits")] public decimal? TaxableBenefits { get; init; }
    [JsonPropertyName("grossMedicalInsurance")] public decimal? GrossMedicalInsurance { get; init; }
}

public sealed class PayrollSubmissionRequestDto
{
    [JsonPropertyName("payslips")] public required List<PayslipSubmissionDto> Payslips { get; init; }
    [JsonPropertyName("lineItemIDsToDelete")] public List<string> LineItemIdsToDelete { get; init; } = [];
}

public sealed class AcknowledgementResponseDto
{
    [JsonPropertyName("acknowledgementStatus")] public required string AcknowledgementStatus { get; init; }
    [JsonPropertyName("acknowledgementID")] public string? AcknowledgementId { get; init; }
    [JsonPropertyName("errors")] public List<ValidationErrorDto>? Errors { get; init; }
}

public sealed class ValidationErrorDto
{
    [JsonPropertyName("code")] public string? Code { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
}

public sealed class SubmissionSummaryDto
{
    [JsonPropertyName("taxOnIncome")] public decimal TaxOnIncome { get; init; }
    [JsonPropertyName("prsi")] public decimal Prsi { get; init; }
    [JsonPropertyName("usc")] public decimal Usc { get; init; }
    [JsonPropertyName("lpt")] public decimal Lpt { get; init; }
    [JsonPropertyName("payslipCount")] public int PayslipCount { get; init; }
    [JsonPropertyName("payslipToDeleteCount")] public int PayslipToDeleteCount { get; init; }
}

public sealed class CheckPayrollSubmissionResponseDto
{
    [JsonPropertyName("submissionID")] public required string SubmissionId { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("submissionSummary")] public SubmissionSummaryDto? SubmissionSummary { get; init; }
    [JsonPropertyName("errors")] public List<ValidationErrorDto>? Errors { get; init; }
}

public sealed class CheckPayrollRunResponseDto
{
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("taxOnIncome")] public decimal TaxOnIncome { get; init; }
    [JsonPropertyName("prsi")] public decimal Prsi { get; init; }
    [JsonPropertyName("usc")] public decimal Usc { get; init; }
    [JsonPropertyName("lpt")] public decimal Lpt { get; init; }
    [JsonPropertyName("submissions")] public List<CheckPayrollSubmissionResponseDto> Submissions { get; init; } = [];
}

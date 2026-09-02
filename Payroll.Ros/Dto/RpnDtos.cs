using System.Text.Json.Serialization;

namespace Payroll.Ros.Dto;

public sealed class EmployeeIdDto
{
    [JsonPropertyName("employeePpsn")] public required string EmployeePpsn { get; init; }
    [JsonPropertyName("employmentID")] public required string EmploymentId { get; init; }
}

public sealed class NameDto
{
    [JsonPropertyName("firstName")] public required string FirstName { get; init; }
    [JsonPropertyName("familyName")] public required string FamilyName { get; init; }
}

public sealed class RateBandDto
{
    [JsonPropertyName("index")] public required int Index { get; init; }
    [JsonPropertyName("taxRatePercent")] public decimal? TaxRatePercent { get; init; }
    [JsonPropertyName("uscRatePercent")] public decimal? UscRatePercent { get; init; }
    [JsonPropertyName("yearlyRateCutOff")] public decimal? YearlyRateCutOff { get; init; }
    [JsonPropertyName("yearlyUSCRateCutOff")] public decimal? YearlyUscRateCutOff { get; init; }
}

public sealed class RpnItemDto
{
    [JsonPropertyName("rpnNumber")] public required string RpnNumber { get; init; }
    [JsonPropertyName("employeeID")] public required EmployeeIdDto EmployeeId { get; init; }
    [JsonPropertyName("rpnIssueDate")] public required string RpnIssueDate { get; init; }
    [JsonPropertyName("name")] public required NameDto Name { get; init; }
    [JsonPropertyName("effectiveDate")] public required string EffectiveDate { get; init; }
    [JsonPropertyName("endDate")] public string? EndDate { get; init; }
    [JsonPropertyName("incomeTaxCalculationBasis")] public required string IncomeTaxCalculationBasis { get; init; }
    [JsonPropertyName("yearlyTaxCredits")] public decimal YearlyTaxCredits { get; init; }
    [JsonPropertyName("taxRates")] public required List<RateBandDto> TaxRates { get; init; }
    [JsonPropertyName("payForIncomeTaxToDate")] public decimal PayForIncomeTaxToDate { get; init; }
    [JsonPropertyName("incomeTaxDeductedToDate")] public decimal IncomeTaxDeductedToDate { get; init; }
    [JsonPropertyName("uscStatus")] public required string UscStatus { get; init; }
    [JsonPropertyName("uscRates")] public List<RateBandDto> UscRates { get; init; } = [];
    [JsonPropertyName("payForUSCToDate")] public decimal PayForUscToDate { get; init; }
    [JsonPropertyName("uscDeductedToDate")] public decimal UscDeductedToDate { get; init; }
}

public sealed class RpnLookupResponseDto
{
    [JsonPropertyName("employerName")] public string? EmployerName { get; init; }
    [JsonPropertyName("employerRegistrationNumber")] public required string EmployerRegistrationNumber { get; init; }
    [JsonPropertyName("taxYear")] public int TaxYear { get; init; }
    [JsonPropertyName("totalRPNCount")] public int TotalRpnCount { get; init; }
    [JsonPropertyName("dateTimeEffective")] public string? DateTimeEffective { get; init; }
    [JsonPropertyName("rpns")] public List<RpnItemDto> Rpns { get; init; } = [];
}

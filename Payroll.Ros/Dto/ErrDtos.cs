using System.Text.Json.Serialization;

namespace Payroll.Ros.Dto;

public sealed class AddressLineDto
{
    [JsonPropertyName("addressLine")] public required string AddressLine { get; init; }
}

public sealed class AddressDto
{
    [JsonPropertyName("addressLines")] public required List<AddressLineDto> AddressLines { get; init; }
    [JsonPropertyName("county")] public required string County { get; init; }
    [JsonPropertyName("eircode")] public string? Eircode { get; init; }
    [JsonPropertyName("countryCode")] public required string CountryCode { get; init; }
}

public sealed class ExpenseBenefitItemDto
{
    [JsonPropertyName("lineItemID")] public required string LineItemId { get; init; }
    [JsonPropertyName("employeeID")] public required EmployeeIdDto EmployeeId { get; init; }
    [JsonPropertyName("employerReference")] public required string EmployerReference { get; init; }
    [JsonPropertyName("name")] public required NameDto Name { get; init; }
    [JsonPropertyName("address")] public required AddressDto Address { get; init; }
    [JsonPropertyName("dateOfBirth")] public required string DateOfBirth { get; init; }
    [JsonPropertyName("category")] public required string Category { get; init; }
    [JsonPropertyName("subCategory")] public string? SubCategory { get; init; }
    [JsonPropertyName("numberOfDays")] public int? NumberOfDays { get; init; }
    [JsonPropertyName("paymentDate")] public required string PaymentDate { get; init; }
    [JsonPropertyName("amount")] public required decimal Amount { get; init; }
}

public sealed class ErrSubmissionRequestDto
{
    [JsonPropertyName("expensesBenefits")] public required List<ExpenseBenefitItemDto> ExpensesBenefits { get; init; }
}

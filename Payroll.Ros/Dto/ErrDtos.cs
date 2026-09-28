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

/// <summary>The amount is only included once every submission in the run has been processed.</summary>
public sealed class CheckErrRunResponseDto
{
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("amount")] public decimal? Amount { get; init; }
    [JsonPropertyName("expenseBenefitSubmissions")] public List<CheckErrSubmissionResponseDto> Submissions { get; init; } = [];
    [JsonPropertyName("validationErrors")] public List<ValidationErrorDto>? ValidationErrors { get; init; }
}

public sealed class CheckErrSubmissionResponseDto
{
    [JsonPropertyName("submissionID")] public required string SubmissionId { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("expenseBenefitSubmissionSummary")] public ErrSubmissionSummaryDto? Summary { get; init; }

    /// <summary>Items ROS rejected - these were NOT saved, even if the submission status is COMPLETED.</summary>
    [JsonPropertyName("invalidExpensesBenefits")] public List<InvalidExpenseBenefitDto>? InvalidItems { get; init; }

    /// <summary>Warnings on items ROS did save.</summary>
    [JsonPropertyName("expenseBenefitWarnings")] public List<ExpenseBenefitWarningDto>? Warnings { get; init; }

    [JsonPropertyName("validationErrors")] public List<ValidationErrorDto>? ValidationErrors { get; init; }
}

public sealed class ErrSubmissionSummaryDto
{
    [JsonPropertyName("amount")] public decimal Amount { get; init; }
    [JsonPropertyName("expensesBenefitsCount")] public int Count { get; init; }
    [JsonPropertyName("expensesBenefitsToDeleteCount")] public int ToDeleteCount { get; init; }
}

public sealed class InvalidExpenseBenefitDto
{
    [JsonPropertyName("lineItemID")] public string? LineItemId { get; init; }
    [JsonPropertyName("errors")] public List<ValidationErrorDto> Errors { get; init; } = [];
}

public sealed class ExpenseBenefitWarningDto
{
    [JsonPropertyName("lineItemID")] public string? LineItemId { get; init; }
    [JsonPropertyName("warnings")] public List<ValidationErrorDto> Warnings { get; init; } = [];
}

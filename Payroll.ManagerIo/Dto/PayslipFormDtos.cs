using System.Text.Json.Serialization;

namespace Payroll.ManagerIo.Dto;

// Field names/casing here mirror exactly what GET /payslip-form/{key} returns on a live Manager.io
// instance - Manager's API is not consistently cased (e.g. "employee" but "Earnings"), so this is
// copied from an observed example rather than a documented spec.

public sealed class EarningsLineDto
{
    [JsonPropertyName("Description")] public required string Description { get; init; }
    [JsonPropertyName("UnitPrice")] public required decimal UnitPrice { get; init; }
}

public sealed class DeductionLineDto
{
    [JsonPropertyName("Item")] public required string Item { get; init; }
    [JsonPropertyName("Description")] public required string Description { get; init; }
    [JsonPropertyName("DeductionAmount")] public required decimal DeductionAmount { get; init; }
}

public sealed class PayslipFormDto
{
    [JsonPropertyName("Date")] public required string Date { get; init; }
    [JsonPropertyName("employee")] public required string Employee { get; init; }
    [JsonPropertyName("Earnings")] public required List<EarningsLineDto> Earnings { get; init; }
    [JsonPropertyName("Deductions")] public required List<DeductionLineDto> Deductions { get; init; }
    [JsonPropertyName("Contributions")] public List<object> Contributions { get; init; } = [];
    [JsonPropertyName("CustomFields2")] public object CustomFields2 { get; init; } = new();
}

public sealed class PaymentLineDto
{
    [JsonPropertyName("Account")] public required string Account { get; init; }
    [JsonPropertyName("Employee")] public string? Employee { get; init; }
    [JsonPropertyName("CustomFields2")] public object CustomFields2 { get; init; } = new();
    [JsonPropertyName("Amount")] public required decimal Amount { get; init; }
}

public sealed class PaymentFormDto
{
    [JsonPropertyName("Date")] public required string Date { get; init; }
    [JsonPropertyName("PaidFrom")] public required string PaidFrom { get; init; }
    [JsonPropertyName("Description")] public required string Description { get; init; }
    [JsonPropertyName("Lines")] public required List<PaymentLineDto> Lines { get; init; }
    [JsonPropertyName("CustomFields2")] public object CustomFields2 { get; init; } = new();
}

public sealed class FormCreatedResponseDto
{
    [JsonPropertyName("id")] public string? Id { get; init; }
}

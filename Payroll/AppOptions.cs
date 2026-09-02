namespace Payroll;

public sealed class EmployerOptions
{
    public string RegistrationNumber { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public string VatRegistrationNumber { get; set; } = "";
}

/// <summary>A recurring Benefit in Kind default, e.g. a monthly health insurance premium. Category must
/// be "General" or "MedicalInsurance" - see Payroll.Core.BikCategory.</summary>
public sealed class BenefitInKindDefaultOptions
{
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public string Category { get; set; } = "General";
}

public sealed class EmployeeOptions
{
    public string Ppsn { get; set; } = "";
    public string EmploymentId { get; set; } = "1";
    public string FirstName { get; set; } = "";
    public string FamilyName { get; set; } = "";
    public decimal DefaultMonthlyGross { get; set; }
    public decimal DefaultMonthlyPensionContribution { get; set; }
    public int DefaultMonthlyEworkingDays { get; set; }
    public decimal EworkingDailyRate { get; set; }

    /// <summary>Recurring Benefits in Kind (e.g. health insurance). Add a new entry here (Description,
    /// Amount, Category) plus a matching key in ManagerIo:BenefitInKindDeductionItemKeys to add a new
    /// recurring benefit - no code change needed. One-off/occasional benefits can instead be added
    /// interactively at the review screen without touching config at all.</summary>
    public List<BenefitInKindDefaultOptions> DefaultBenefitsInKind { get; set; } = [];

    public string DateOfBirth { get; set; } = "";
    public List<string> AddressLines { get; set; } = [];
    public string County { get; set; } = "";
    public string Eircode { get; set; } = "";
    public string CountryCode { get; set; } = "IRL";
}

public sealed class RosConfigOptions
{
    public string SoftwareUsed { get; set; } = "";
    public string SoftwareVersion { get; set; } = "";
    public string Environment { get; set; } = "Pit";
    public string P12Path { get; set; } = "";

    /// <summary>Set via `dotnet user-secrets set "Ros:P12PlainPassword" "..."` - never stored in appsettings.json.</summary>
    public string P12PlainPassword { get; set; } = "";
}

public sealed class ManagerIoConfigOptions
{
    public string BaseUrl { get; set; } = "";

    /// <summary>Set via `dotnet user-secrets set "ManagerIo:ApiKey" "..."` - never stored in appsettings.json.</summary>
    public string ApiKey { get; set; } = "";

    public string EmployeeKey { get; set; } = "";
    public string BankAccountKey { get; set; } = "";
    public string PaymentClearingAccountKey { get; set; } = "";
    public string PensionDeductionItemKey { get; set; } = "";
    public string PayeDeductionItemKey { get; set; } = "";
    public string UscDeductionItemKey { get; set; } = "";
    public string PrsiDeductionItemKey { get; set; } = "";
    public Dictionary<string, string> BenefitInKindDeductionItemKeys { get; set; } = [];
    public string? VatPayableAccountKey { get; set; }
    public string? VatRoundingAdjustmentAccountKey { get; set; }
}

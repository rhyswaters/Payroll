namespace Payroll.Ros;

public sealed record ErrAddress(IReadOnlyList<string> AddressLines, string County, string CountryCode, string? Eircode = null);

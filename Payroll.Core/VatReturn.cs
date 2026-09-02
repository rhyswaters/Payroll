namespace Payroll.Core;

/// <summary>
/// A VAT3 return for one bi-monthly period. ROS's VAT3 form only accepts whole euro for sales/purchases
/// VAT, so the real amount paid never quite matches the unrounded liability accrued in the books - the
/// difference is <see cref="RoundingAdjustment"/>, which needs its own line when reconciling.
/// </summary>
public sealed record VatReturn(
    string CompanyName,
    string VatRegistrationNumber,
    VatPeriod Period,
    decimal SalesVat,
    decimal PurchasesVat)
{
    public int RoundedSalesVat => RoundToNearestEuro(SalesVat);
    public int RoundedPurchasesVat => RoundToNearestEuro(PurchasesVat);

    /// <summary>What ROS actually calculates as owed from the two rounded figures - this is the real
    /// amount that gets paid.</summary>
    public int NetPayable => RoundedSalesVat - RoundedPurchasesVat;

    /// <summary>The true VAT liability accrued in the books for this period, unrounded - this is what
    /// should be cleared from the VAT Payable account.</summary>
    public decimal UnroundedNetLiability => SalesVat - PurchasesVat;

    /// <summary>Plug for the reconciling payment: positive if you pay slightly more than the books show
    /// as owed (posts as a small expense), negative if slightly less (posts as a small credit/gain).</summary>
    public decimal RoundingAdjustment => NetPayable - UnroundedNetLiability;

    private static int RoundToNearestEuro(decimal value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}

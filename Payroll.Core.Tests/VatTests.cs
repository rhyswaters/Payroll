using Payroll.Core;
using Xunit;

namespace Payroll.Core.Tests;

public class VatPeriodTests
{
    [Theory]
    [InlineData(2026, 9, 1, 2026, 7, 1, 2026, 8, 31)]   // Sep 1: current period Sep-Oct in progress, most recent completed is Jul-Aug
    [InlineData(2026, 9, 5, 2026, 7, 1, 2026, 8, 31)]   // Sep 5: same - not Sep 1-5
    [InlineData(2026, 10, 31, 2026, 7, 1, 2026, 8, 31)] // still within Sep-Oct
    [InlineData(2026, 11, 1, 2026, 9, 1, 2026, 10, 31)] // rolled into Nov-Dec, most recent completed is Sep-Oct
    [InlineData(2027, 1, 1, 2026, 11, 1, 2026, 12, 31)] // year boundary
    [InlineData(2026, 3, 1, 2026, 1, 1, 2026, 2, 28)]
    public void MostRecentlyCompleted_ReturnsThePriorTwoMonthBlock(
        int y, int m, int d, int startY, int startM, int startD, int endY, int endM, int endD)
    {
        var today = new DateOnly(y, m, d);
        var period = VatPeriod.MostRecentlyCompleted(today);
        Assert.Equal(new DateOnly(startY, startM, startD), period.Start);
        Assert.Equal(new DateOnly(endY, endM, endD), period.End);
    }

    [Theory]
    [InlineData(2026, 9, 5, 2026, 9, 1, 2026, 10, 31)]
    [InlineData(2026, 1, 1, 2026, 1, 1, 2026, 2, 28)]
    public void Containing_ReturnsTheInProgressBlock(
        int y, int m, int d, int startY, int startM, int startD, int endY, int endM, int endD)
    {
        var period = VatPeriod.Containing(new DateOnly(y, m, d));
        Assert.Equal(new DateOnly(startY, startM, startD), period.Start);
        Assert.Equal(new DateOnly(endY, endM, endD), period.End);
    }
}

public class VatReturnTests
{
    [Fact]
    public void Build_MatchesRealBulletHqGeneratedFile()
    {
        var vatReturn = new VatReturn(
            "RhysCom Ltd", "3339654TH",
            new VatPeriod(new DateOnly(2026, 7, 1), new DateOnly(2026, 8, 31)),
            SalesVat: 6578m, PurchasesVat: 51m);

        var xml = Vat3XmlWriter.Build(vatReturn);

        const string expected = "<?xml version=\"1.0\" encoding=\"UTF-8\"?> <VAT3 name=\"RhysCom Ltd\" regnum=\"3339654TH\" " +
            "startdate=\"01/07/2026\" enddate=\"31/08/2026\" sales=\"6578\" purchs=\"51\" goodsto=\"0\" goodsfrom=\"0\" " +
            "servicesto=\"0\" servicesfrom=\"0\" postponedAccounting=\"0\" currency=\"E\" type=\"0\" filefreq=\"0\" " +
            "formversion=\"1\" language=\"E\" />";

        Assert.Equal(expected, xml);
    }

    [Fact]
    public void RoundingAdjustment_IsZero_WhenFiguresAreAlreadyWholeEuro()
    {
        var vatReturn = new VatReturn("RhysCom Ltd", "3339654TH",
            new VatPeriod(new DateOnly(2026, 7, 1), new DateOnly(2026, 8, 31)), 6578m, 51m);

        Assert.Equal(6527, vatReturn.NetPayable);
        Assert.Equal(6527m, vatReturn.UnroundedNetLiability);
        Assert.Equal(0m, vatReturn.RoundingAdjustment);
    }

    [Fact]
    public void RoundingAdjustment_CanBeNegative_WhenRoundingReducesTheAmountOwed()
    {
        // Sales rounds down (6578.20 -> 6578), purchases rounds up (50.60 -> 51): net paid (6527) is
        // less than the true accrued liability (6527.60), so the adjustment must be negative.
        var vatReturn = new VatReturn("RhysCom Ltd", "3339654TH",
            new VatPeriod(new DateOnly(2026, 7, 1), new DateOnly(2026, 8, 31)), 6578.20m, 50.60m);

        Assert.Equal(6527, vatReturn.NetPayable);
        Assert.Equal(6527.60m, vatReturn.UnroundedNetLiability);
        Assert.Equal(-0.60m, vatReturn.RoundingAdjustment);
    }
}

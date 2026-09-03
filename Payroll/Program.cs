using Microsoft.Extensions.Configuration;
using Payroll;
using Payroll.Core;
using Payroll.ManagerIo;
using Payroll.Ros;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

var employer = configuration.GetSection("Employer").Get<EmployerOptions>() ?? new EmployerOptions();
var employee = configuration.GetSection("Employee").Get<EmployeeOptions>() ?? new EmployeeOptions();
var rosConfig = configuration.GetSection("Ros").Get<RosConfigOptions>() ?? new RosConfigOptions();
var managerIoConfig = configuration.GetSection("ManagerIo").Get<ManagerIoConfigOptions>() ?? new ManagerIoConfigOptions();
var storageConfig = configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();

var missing = new List<string>();
if (string.IsNullOrWhiteSpace(employer.RegistrationNumber)) missing.Add("Employer:RegistrationNumber");
if (string.IsNullOrWhiteSpace(employee.Ppsn)) missing.Add("Employee:Ppsn");
if (string.IsNullOrWhiteSpace(employee.FirstName) || string.IsNullOrWhiteSpace(employee.FamilyName)) missing.Add("Employee:FirstName/FamilyName");
if (string.IsNullOrWhiteSpace(rosConfig.P12Path)) missing.Add("Ros:P12Path");
if (string.IsNullOrWhiteSpace(rosConfig.P12PlainPassword)) missing.Add("Ros:P12PlainPassword (set with dotnet user-secrets)");
if (string.IsNullOrWhiteSpace(managerIoConfig.BaseUrl)) missing.Add("ManagerIo:BaseUrl");
if (string.IsNullOrWhiteSpace(managerIoConfig.ApiKey)) missing.Add("ManagerIo:ApiKey (set with dotnet user-secrets)");
if (string.IsNullOrWhiteSpace(managerIoConfig.EmployeeKey)) missing.Add("ManagerIo:EmployeeKey");
if (string.IsNullOrWhiteSpace(managerIoConfig.BankAccountKey)) missing.Add("ManagerIo:BankAccountKey");
if (string.IsNullOrWhiteSpace(employee.DateOfBirth)) missing.Add("Employee:DateOfBirth (needed for Enhanced Reporting Requirements submissions)");
if (employee.AddressLines.Count == 0 || string.IsNullOrWhiteSpace(employee.County)) missing.Add("Employee:AddressLines/County (needed for Enhanced Reporting Requirements submissions)");

if (missing.Count > 0)
{
    Console.WriteLine("Missing required configuration:");
    foreach (var m in missing) Console.WriteLine($"  - {m}");
    Console.WriteLine();
    Console.WriteLine("Fill in the non-secret values in appsettings.json.");
    Console.WriteLine("Set secrets from the Payroll/ project directory, e.g.:");
    Console.WriteLine("  dotnet user-secrets set \"Ros:P12PlainPassword\" \"your-ros-password\"");
    Console.WriteLine("  dotnet user-secrets set \"ManagerIo:ApiKey\" \"your-manager-io-key\"");
    return 1;
}

var employeeId = new EmployeeId(employee.Ppsn, employee.EmploymentId);

using var ros = new RosClient(new RosOptions
{
    EmployerRegistrationNumber = employer.RegistrationNumber,
    SoftwareUsed = rosConfig.SoftwareUsed,
    SoftwareVersion = rosConfig.SoftwareVersion,
    P12Path = rosConfig.P12Path,
    P12PlainPassword = rosConfig.P12PlainPassword,
    Environment = Enum.Parse<RosEnvironment>(rosConfig.Environment, ignoreCase: true)
});

var dataDir = string.IsNullOrWhiteSpace(storageConfig.DataDirectory)
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Payroll")
    : storageConfig.DataDirectory;
Directory.CreateDirectory(dataDir);
var ytdStore = new YearToDateStore(Path.Combine(dataDir, "year-to-date.json"));

if (args.Contains("--list-rpns"))
{
    var year = DateTime.Today.Year;
    var all = await ros.ListAllRpnsAsync(year);
    Console.WriteLine($"ROS holds {all.Count} RPN(s) for employer {employer.RegistrationNumber}, tax year {year}:");
    foreach (var r in all)
        Console.WriteLine($"  PPSN={r.EmployeeId.EmployeePpsn} EmploymentID={r.EmployeeId.EmploymentId} RPN={r.RpnNumber} YearlyCredits={r.YearlyTaxCredits:C}");
    return 0;
}

if (args.Contains("--show-ytd"))
{
    var year = DateTime.Today.Year;
    var current = ytdStore.Get(year);
    Console.WriteLine($"Locally tracked year-to-date totals for {year}:");
    Console.WriteLine($"  Pay for income tax to date: {current.PayForIncomeTaxToDate:C}");
    Console.WriteLine($"  Income tax deducted to date: {current.IncomeTaxDeductedToDate:C}");
    Console.WriteLine($"  Pay for USC to date:         {current.PayForUscToDate:C}");
    Console.WriteLine($"  USC deducted to date:        {current.UscDeductedToDate:C}");
    Console.WriteLine($"  PRSI deducted to date:       {current.PrsiDeductedToDate:C}");
    return 0;
}

if (args.Contains("--seed-ytd"))
{
    var year = DateTime.Today.Year;
    Console.WriteLine($"Enter year-to-date totals for {year} as of the last real payslip BEFORE the one you're about to run:");
    var seeded = new YearToDateTotals(
        PromptDecimal("Pay for income tax to date"),
        PromptDecimal("Income tax deducted to date"),
        PromptDecimal("Pay for USC to date"),
        PromptDecimal("USC deducted to date"),
        PromptDecimal("PRSI deducted to date (informational only, doesn't affect any calculation)"));
    ytdStore.Set(year, seeded);
    Console.WriteLine("Saved.");
    return 0;
}

if (args.Contains("--summary"))
{
    var year = DateTime.Today.Year;
    var ytd = ytdStore.Get(year);
    Console.WriteLine($"=== Payroll, {year} year-to-date ===");
    Console.WriteLine($"PAYE deducted:  {ytd.IncomeTaxDeductedToDate,10:C}");
    Console.WriteLine($"USC deducted:   {ytd.UscDeductedToDate,10:C}");
    Console.WriteLine($"PRSI deducted:  {ytd.PrsiDeductedToDate,10:C}");
    Console.WriteLine($"Total:          {ytd.IncomeTaxDeductedToDate + ytd.UscDeductedToDate + ytd.PrsiDeductedToDate,10:C}");

    var today = DateOnly.FromDateTime(DateTime.Today);
    var currentPeriod = VatPeriod.Containing(today);
    Console.WriteLine();
    Console.WriteLine($"=== VAT, current period so far ({currentPeriod.Start:dd/MM/yyyy} - {today:dd/MM/yyyy}, period ends {currentPeriod.End:dd/MM/yyyy}) ===");

    using var managerIoForSummary = new ManagerIoClient(new ManagerIoOptions
    {
        BaseUrl = managerIoConfig.BaseUrl,
        ApiKey = managerIoConfig.ApiKey,
        EmployeeKey = managerIoConfig.EmployeeKey,
        BankAccountKey = managerIoConfig.BankAccountKey,
        PaymentClearingAccountKey = managerIoConfig.PaymentClearingAccountKey,
        PensionDeductionItemKey = managerIoConfig.PensionDeductionItemKey,
        PayeDeductionItemKey = managerIoConfig.PayeDeductionItemKey,
        UscDeductionItemKey = managerIoConfig.UscDeductionItemKey,
        PrsiDeductionItemKey = managerIoConfig.PrsiDeductionItemKey,
        BenefitInKindDeductionItemKeys = managerIoConfig.BenefitInKindDeductionItemKeys,
        VatPayableAccountKey = managerIoConfig.VatPayableAccountKey,
        VatRoundingAdjustmentAccountKey = managerIoConfig.VatRoundingAdjustmentAccountKey
    });

    try
    {
        var figures = await managerIoForSummary.GetVatFiguresAsync(currentPeriod.Start, today);
        Console.WriteLine($"Sales VAT so far:     {figures.SalesVat,10:C}");
        Console.WriteLine($"Purchases VAT so far: {figures.PurchasesVat,10:C}");
        Console.WriteLine($"Net position so far:  {figures.SalesVat - figures.PurchasesVat,10:C} (running - period isn't closed, don't file this)");
        if (figures.UnexpectedLines.Count > 0)
            Console.WriteLine($"Note: {figures.UnexpectedLines.Count} entries from other transaction types found - not included above, see --vat-return closer to period end.");
    }
    catch (Exception ex) when (ex is HttpRequestException or ManagerIoClientException)
    {
        Console.WriteLine($"Could not reach Manager.io for the VAT position: {ex.Message}");
    }

    return 0;
}

if (args.Contains("--vat-return"))
{
    var period = VatPeriod.MostRecentlyCompleted(DateOnly.FromDateTime(DateTime.Today));
    Console.WriteLine($"VAT period: {period.Start:dd/MM/yyyy} - {period.End:dd/MM/yyyy}");

    using var managerIoForVat = new ManagerIoClient(new ManagerIoOptions
    {
        BaseUrl = managerIoConfig.BaseUrl,
        ApiKey = managerIoConfig.ApiKey,
        EmployeeKey = managerIoConfig.EmployeeKey,
        BankAccountKey = managerIoConfig.BankAccountKey,
        PaymentClearingAccountKey = managerIoConfig.PaymentClearingAccountKey,
        PensionDeductionItemKey = managerIoConfig.PensionDeductionItemKey,
        PayeDeductionItemKey = managerIoConfig.PayeDeductionItemKey,
        UscDeductionItemKey = managerIoConfig.UscDeductionItemKey,
        PrsiDeductionItemKey = managerIoConfig.PrsiDeductionItemKey,
        BenefitInKindDeductionItemKeys = managerIoConfig.BenefitInKindDeductionItemKeys,
        VatPayableAccountKey = managerIoConfig.VatPayableAccountKey,
        VatRoundingAdjustmentAccountKey = managerIoConfig.VatRoundingAdjustmentAccountKey
    });

    VatFigures figures;
    try
    {
        figures = await managerIoForVat.GetVatFiguresAsync(period.Start, period.End);
    }
    catch (Exception ex) when (ex is HttpRequestException or ManagerIoClientException)
    {
        Console.WriteLine($"Could not pull VAT figures from Manager.io: {ex.Message}");
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine("Sales VAT (from Receipts):");
    foreach (var l in figures.SalesLines) Console.WriteLine($"  {l.Date:yyyy-MM-dd}  {l.Amount,10:C}");
    Console.WriteLine($"  Total: {figures.SalesVat:C}");
    Console.WriteLine();
    Console.WriteLine("Purchases VAT (from Payments):");
    foreach (var l in figures.PurchaseLines) Console.WriteLine($"  {l.Date:yyyy-MM-dd}  {l.Amount,10:C}");
    Console.WriteLine($"  Total: {figures.PurchasesVat:C}");

    if (figures.UnexpectedLines.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("WARNING: found VAT Payable entries from transaction types this tool doesn't include automatically:");
        foreach (var l in figures.UnexpectedLines) Console.WriteLine($"  [{l.Source}] {l.Date:yyyy-MM-dd}  {l.Amount,10:C}");
        Console.WriteLine("Review these manually - they are NOT included in the totals above.");
    }

    var vatReturn = new VatReturn(employer.CompanyName, employer.VatRegistrationNumber, period, figures.SalesVat, figures.PurchasesVat);

    Console.WriteLine();
    Console.WriteLine($"VAT3 return for {vatReturn.Period.Start:dd/MM/yyyy} - {vatReturn.Period.End:dd/MM/yyyy}:");
    Console.WriteLine($"  Sales (T1):      {vatReturn.RoundedSalesVat}");
    Console.WriteLine($"  Purchases (T2):  {vatReturn.RoundedPurchasesVat}");
    Console.WriteLine($"  Net payable:     {vatReturn.NetPayable}");
    Console.WriteLine($"  (unrounded books liability: {vatReturn.UnroundedNetLiability:C}, rounding adjustment: {vatReturn.RoundingAdjustment:C})");

    var vatDir = Path.Combine(dataDir, "vat-returns");
    Directory.CreateDirectory(vatDir);
    var xmlPath = Path.Combine(vatDir, $"VAT3-{period.Start:yyyyMM}-{period.End:yyyyMM}.xml");
    File.WriteAllText(xmlPath, Vat3XmlWriter.Build(vatReturn));
    Console.WriteLine();
    Console.WriteLine($"XML written to: {xmlPath}");

    Console.WriteLine();
    Console.WriteLine("Upload this file to ROS (My Services -> Complete a Form Online / File a Return -> VAT3)");
    Console.WriteLine("and submit your payment there now.");
    Console.WriteLine("Note: ROS doesn't reliably debit on the date you specify - check your bank statement in a");
    Console.WriteLine("few days and correct the date on this payment directly in Manager.io if it's actually different.");
    Console.Write("Enter the date you actually paid on ROS (yyyy-MM-dd) [today], or type 'cancel' to skip recording a payment: ");
    var paymentDateInput = (Console.ReadLine() ?? "").Trim();
    if (paymentDateInput.Equals("cancel", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Not recording a reconciling payment. Run --vat-return again once you've paid, or record it manually in Manager.io.");
        return 0;
    }
    var paymentDate = string.IsNullOrWhiteSpace(paymentDateInput)
        ? DateOnly.FromDateTime(DateTime.Today)
        : DateOnly.TryParse(paymentDateInput, out var pd) ? pd : DateOnly.FromDateTime(DateTime.Today);

    try
    {
        var reconciliationKey = await managerIoForVat.CreateVatReconciliationPaymentAsync(
            paymentDate, vatReturn, $"VAT payment {period.Start:MMM yyyy} - {period.End:MMM yyyy}");
        Console.WriteLine($"Manager.io reconciling payment created: {reconciliationKey}");
        Console.WriteLine($"VAT Payable cleared by {vatReturn.UnroundedNetLiability:C}; rounding adjustment of {vatReturn.RoundingAdjustment:C} booked.");
    }
    catch (ManagerIoClientException ex)
    {
        Console.WriteLine($"Could not record the reconciling payment in Manager.io: {ex.Message}");
        return 1;
    }

    return 0;
}

// Tax year is derived from the pay date, not "today" - if payroll ever runs a few days late for a
// payslip actually dated in the previous year, this keeps the RPN lookup and YTD tracking correct.
var payDate = DateOnly.FromDateTime(DateTime.Today);
var taxYear = payDate.Year;

Console.WriteLine($"Fetching current RPN for {employee.FirstName} {employee.FamilyName} ({employeeId}) - tax year {taxYear}, {rosConfig.Environment} environment...");

RpnDetails rpn;
try
{
    rpn = await ros.LookupRpnAsync(taxYear, employeeId);
}
catch (RosClientException ex)
{
    Console.WriteLine($"Could not fetch RPN from ROS: {ex.Message}");
    return 1;
}

Console.WriteLine($"RPN {rpn.RpnNumber} (issued {rpn.RpnIssueDate:yyyy-MM-dd}): yearly tax credits {rpn.YearlyTaxCredits:C}, USC status {rpn.UscStatus}");

var startingYtd = ytdStore.Get(taxYear);
Console.WriteLine($"Locally tracked year-to-date before this payslip: pay for tax {startingYtd.PayForIncomeTaxToDate:C}, " +
                   $"PAYE deducted {startingYtd.IncomeTaxDeductedToDate:C}, pay for USC {startingYtd.PayForUscToDate:C}, USC deducted {startingYtd.UscDeductedToDate:C}");
Console.WriteLine("(run with --show-ytd to see this any time, or --seed-ytd to correct it)");
Console.WriteLine();

var gross = employee.DefaultMonthlyGross;
var pension = employee.DefaultMonthlyPensionContribution;
var eworkingDays = employee.DefaultMonthlyEworkingDays;
var benefitsInKind = employee.DefaultBenefitsInKind
    .Select(b => new BenefitInKindLine(b.Description, b.Amount, Enum.Parse<BikCategory>(b.Category)))
    .ToList();

PayslipResult result = Recalculate();

while (true)
{
    PrintPayslip(result);
    Console.WriteLine();
    Console.Write("[A]pprove, [G]ross pay, [P]ension, [E]working days, [B]enefits in kind, [D]ate, [Q]uit: ");
    var choice = (Console.ReadLine() ?? "").Trim().ToUpperInvariant();

    if (choice == "A")
    {
        break;
    }
    if (choice == "Q")
    {
        Console.WriteLine("Cancelled - nothing was submitted.");
        return 0;
    }
    if (choice == "G")
    {
        Console.Write($"New gross pay [{gross:0.00}]: ");
        if (decimal.TryParse(Console.ReadLine(), out var g)) gross = g;
        result = Recalculate();
    }
    else if (choice == "P")
    {
        Console.Write($"New pension contribution [{pension:0.00}]: ");
        if (decimal.TryParse(Console.ReadLine(), out var p)) pension = p;
        result = Recalculate();
    }
    else if (choice == "E")
    {
        Console.Write($"New e-working days [{eworkingDays}]: ");
        if (int.TryParse(Console.ReadLine(), out var e)) eworkingDays = e;
        result = Recalculate();
    }
    else if (choice == "B")
    {
        if (benefitsInKind.Count == 0)
            Console.WriteLine("No benefits in kind on this payslip.");
        else
            for (var i = 0; i < benefitsInKind.Count; i++)
                Console.WriteLine($"  [{i}] {benefitsInKind[i].Description}: {benefitsInKind[i].Amount:C} ({benefitsInKind[i].Category})");

        Console.Write("Enter index to edit/remove, 'new' to add, or blank to cancel: ");
        var bikInput = (Console.ReadLine() ?? "").Trim();

        if (bikInput.Equals("new", StringComparison.OrdinalIgnoreCase))
        {
            Console.Write("Description: ");
            var description = (Console.ReadLine() ?? "").Trim();
            Console.Write("Amount: ");
            decimal.TryParse(Console.ReadLine(), out var amount);
            Console.Write("Category - [G]eneral (a car, accommodation, a loan...) or [M]edical insurance: ");
            var category = (Console.ReadLine() ?? "").Trim().StartsWith("M", StringComparison.OrdinalIgnoreCase)
                ? BikCategory.MedicalInsurance : BikCategory.General;
            if (!string.IsNullOrWhiteSpace(description))
                benefitsInKind.Add(new BenefitInKindLine(description, amount, category));
        }
        else if (int.TryParse(bikInput, out var bikIndex) && bikIndex >= 0 && bikIndex < benefitsInKind.Count)
        {
            Console.Write($"New amount for '{benefitsInKind[bikIndex].Description}' [{benefitsInKind[bikIndex].Amount:0.00}], or 'remove': ");
            var editInput = (Console.ReadLine() ?? "").Trim();
            if (editInput.Equals("remove", StringComparison.OrdinalIgnoreCase))
                benefitsInKind.RemoveAt(bikIndex);
            else if (decimal.TryParse(editInput, out var newAmount))
                benefitsInKind[bikIndex] = benefitsInKind[bikIndex] with { Amount = newAmount };
        }
        result = Recalculate();
    }
    else if (choice == "D")
    {
        Console.Write($"New pay date [{payDate:yyyy-MM-dd}]: ");
        if (DateOnly.TryParse(Console.ReadLine(), out var d))
        {
            payDate = d;
            if (payDate.Year != taxYear)
                Console.WriteLine($"Warning: this date is in {payDate.Year}, but the RPN fetched at startup was for {taxYear}. Restart the app rather than continuing across a tax year boundary.");
        }
        result = Recalculate();
    }
}

if (ros.Options.Environment == RosEnvironment.Production)
{
    Console.WriteLine();
    Console.WriteLine("This will submit real figures to Revenue's live ROS system and cannot be silently undone");
    Console.WriteLine("(a correction submission would be needed to fix a mistake afterward).");
    Console.Write("Type SUBMIT to confirm, anything else to cancel: ");
    if (Console.ReadLine() != "SUBMIT")
    {
        Console.WriteLine("Cancelled - nothing was submitted.");
        return 0;
    }
}

Console.WriteLine();
Console.WriteLine("Submitting payroll to ROS...");

var payrollRunReference = $"PayrollRun-{taxYear}-{payDate:MM}";
var submissionId = $"Submission-{Guid.NewGuid():N}";

try
{
    var acknowledgementId = await ros.CreatePayrollSubmissionAsync(
        taxYear.ToString(), payrollRunReference, submissionId, result);
    Console.WriteLine($"ROS acknowledged the submission: {acknowledgementId}");
}
catch (RosClientException ex)
{
    Console.WriteLine($"ROS rejected the submission: {ex.Message}");
    return 1;
}

ytdStore.Set(taxYear, ytdStore.Get(taxYear).Add(result));

Console.WriteLine("Recording payslip and payment in Manager.io...");

using var managerIo = new ManagerIoClient(new ManagerIoOptions
{
    BaseUrl = managerIoConfig.BaseUrl,
    ApiKey = managerIoConfig.ApiKey,
    EmployeeKey = managerIoConfig.EmployeeKey,
    BankAccountKey = managerIoConfig.BankAccountKey,
    PaymentClearingAccountKey = managerIoConfig.PaymentClearingAccountKey,
    PensionDeductionItemKey = managerIoConfig.PensionDeductionItemKey,
    PayeDeductionItemKey = managerIoConfig.PayeDeductionItemKey,
    UscDeductionItemKey = managerIoConfig.UscDeductionItemKey,
    PrsiDeductionItemKey = managerIoConfig.PrsiDeductionItemKey,
    BenefitInKindDeductionItemKeys = managerIoConfig.BenefitInKindDeductionItemKeys
});

try
{
    var payslipKey = await managerIo.CreatePayslipAsync(result);
    Console.WriteLine($"Manager.io payslip created: {payslipKey}");

    var paymentKey = await managerIo.CreatePaymentAsync(result.Inputs.PayDate, result.NetPay, "Salary");
    Console.WriteLine($"Manager.io payment created: {paymentKey}");
}
catch (ManagerIoClientException ex)
{
    Console.WriteLine($"ROS submission succeeded but Manager.io recording failed: {ex.Message}");
    Console.WriteLine("You'll need to record this payslip/payment in Manager.io manually.");
    return 1;
}

if (eworkingDays > 0)
{
    Console.WriteLine("Reporting remote-working allowance to ROS (Enhanced Reporting Requirements)...");

    var errRunReference = $"ErrRun-{taxYear}-{payDate:MM}";
    var errSubmissionId = $"ErrSubmission-{Guid.NewGuid():N}";
    var address = new ErrAddress(employee.AddressLines, employee.County, employee.CountryCode,
        string.IsNullOrWhiteSpace(employee.Eircode) ? null : employee.Eircode);

    try
    {
        var errAcknowledgementId = await ros.SubmitRemoteWorkingAllowanceAsync(
            taxYear.ToString(), errRunReference, errSubmissionId,
            employeeId, employee.FirstName, employee.FamilyName, address, DateOnly.Parse(employee.DateOfBirth),
            employerReference: employer.RegistrationNumber, eworkingDays, payDate, result.EworkingAllowance);
        Console.WriteLine($"ROS acknowledged the ERR submission: {errAcknowledgementId}");
    }
    catch (RosClientException ex)
    {
        Console.WriteLine($"ROS rejected the ERR submission: {ex.Message}");
        Console.WriteLine("The payroll submission and Manager.io records already went through - only the ERR report needs retrying.");
        return 1;
    }
}

Console.WriteLine();
Console.WriteLine("Done.");
return 0;

PayslipResult Recalculate()
{
    var eworkingAllowance = eworkingDays * employee.EworkingDailyRate;
    var inputs = PayrollInputs.MonthlyFor(
        $"Payslip-{payDate:yyyy-MM}", employeeId, employee.FirstName, employee.FamilyName, payDate, gross, pension,
        eworkingAllowance, benefitsInKind);
    return PayrollCalculator.Calculate(rpn, ytdStore.Get(taxYear), PrsiSettings.ClassS, inputs);
}

static decimal PromptDecimal(string label)
{
    while (true)
    {
        Console.Write($"{label}: ");
        if (decimal.TryParse(Console.ReadLine(), out var value)) return value;
        Console.WriteLine("Not a valid number, try again.");
    }
}

static void PrintPayslip(PayslipResult r)
{
    Console.WriteLine();
    Console.WriteLine($"Pay date:              {r.Inputs.PayDate:yyyy-MM-dd}");
    Console.WriteLine($"Gross pay:             {r.GrossPay,10:C}");
    Console.WriteLine($"e-working allowance:   {r.EworkingAllowance,10:C} (tax-free, reported to ROS via ERR)");
    foreach (var b in r.BenefitsInKind)
        Console.WriteLine($"{b.Description,-23}{b.Amount,10:C} (notional {b.Category} BIK - taxed but not paid in cash)");
    Console.WriteLine($"Pension contribution:  {r.EmployeePensionContribution,10:C}");
    Console.WriteLine($"PAYE (Class {r.PrsiClass}, RPN {r.RpnNumber}): {r.IncomeTax,10:C}");
    Console.WriteLine($"USC:                   {r.Usc,10:C}");
    Console.WriteLine($"PRSI ({r.PrsiRatePercent}%):        {r.EmployeePrsi,10:C}");
    Console.WriteLine($"Net pay:               {r.NetPay,10:C}");
}

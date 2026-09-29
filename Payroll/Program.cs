using Microsoft.Extensions.Configuration;
using Payroll;
using Payroll.Core;
using Payroll.ManagerIo;
using Payroll.Ros;
using Payroll.Ros.Dto;

// No command-line flags given - most launches are Rider's Debug button or double-clicking a compiled
// exe, neither of which lets you pass arguments, so show an interactive menu and keep looping back to
// it after each action instead of running once and exiting. Passing a flag directly still runs once and
// exits, for terminal/scripted use - PromptForMenuChoice's "0. Quit" returns null to end the loop via a
// normal return rather than Environment.Exit, which forces an abrupt process-level exit that some
// terminals (observed with the self-contained published exe) don't recover from cleanly.
var skipPause = false;

if (args.Length == 0)
{
    while (true)
    {
        var choice = PromptForMenuChoice();
        if (choice is null) return 0;

        Console.WriteLine();
        skipPause = false;
        try
        {
            await RunOnce(choice);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected error: {ex.Message}");
        }

        if (!skipPause)
        {
            Console.WriteLine();
            Console.Write("Press Enter to continue...");
            Console.ReadLine();
        }
    }
}

return await RunOnce(args);

async Task<int> RunOnce(string[] args)
{
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
    if (employee.DefaultMonthlyEworkingDays > 0 && string.IsNullOrWhiteSpace(managerIoConfig.EworkingAllowanceAccountKey))
        missing.Add("ManagerIo:EworkingAllowanceAccountKey (the account the e-working allowance payment posts to)");
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
            Console.WriteLine($"  PPSN={r.EmployeeId.EmployeePpsn} EmploymentID={r.EmployeeId.EmploymentId} RPN={r.RpnNumber} issued {r.RpnIssueDate:yyyy-MM-dd} YearlyCredits={r.YearlyTaxCredits:C}");
        return 0;
    }

    if (args.Contains("--check-submission"))
    {
        var year = DateTime.Today.Year;
        Console.Write($"Month to check (1-12) [{DateTime.Today.Month}]: ");
        var monthInput = (Console.ReadLine() ?? "").Trim();
        var month = monthInput == "" ? DateTime.Today.Month : int.TryParse(monthInput, out var m) && m is >= 1 and <= 12 ? m : 0;
        if (month == 0)
        {
            Console.WriteLine("Not a valid month.");
            return 1;
        }

        // Run references must match what the Run payroll option submits under.
        var runReference = $"PayrollRun-{year}-{month:00}";
        var errRunReference = $"ErrRun-{year}-{month:00}";
        var anyInvalid = false;
        var payrollCheckFailed = false;

        CheckPayrollRunResponseDto? run = null;
        try
        {
            run = await ros.CheckPayrollRunAsync(year.ToString(), runReference);
        }
        catch (RosClientException ex)
        {
            Console.WriteLine($"Couldn't check {runReference}: {ex.Message}");
            payrollCheckFailed = true;
        }

        if (run is not null)
        {
            Console.WriteLine($"{runReference}: {run.Status}");
            if (run.TaxOnIncome is not null)
                Console.WriteLine($"  Run totals - PAYE {run.TaxOnIncome:C}, PRSI {run.Prsi:C}, USC {run.Usc:C}, LPT {run.Lpt:C}");
            foreach (var e in run.ValidationErrors ?? [])
                Console.WriteLine($"  ERROR {e.Code} {e.Path}: {e.Description}");

            foreach (var s in run.Submissions)
            {
                var detail = await ros.CheckPayrollSubmissionAsync(year.ToString(), runReference, s.SubmissionId);
                Console.WriteLine();
                Console.WriteLine($"  {detail.SubmissionId}: {detail.Status}");
                if (detail.SubmissionSummary is { } sum)
                    Console.WriteLine($"    {sum.PayslipCount} payslip(s) - PAYE {sum.TaxOnIncome:C}, PRSI {sum.Prsi:C}, USC {sum.Usc:C}");
                foreach (var e in detail.ValidationErrors ?? [])
                    Console.WriteLine($"    ERROR {e.Code} {e.Path}: {e.Description}");
                foreach (var p in detail.InvalidPayslips ?? [])
                {
                    if (run.PayslipSummaries.Any(x => x.LineItemId == p.LineItemId))
                    {
                        Console.WriteLine($"    Rejected payslip {p.LineItemId} - since resubmitted and saved, no action needed:");
                    }
                    else
                    {
                        anyInvalid = true;
                        Console.WriteLine($"    INVALID PAYSLIP {p.LineItemId} (not saved by ROS):");
                    }
                    foreach (var e in p.Errors)
                        Console.WriteLine($"      {e.Code} {e.Path}: {e.Description}");
                }
                foreach (var w in detail.PayslipWarnings ?? [])
                foreach (var e in w.Warnings)
                    Console.WriteLine($"    Warning on {w.LineItemId}: {e.Code} {e.Path}: {e.Description}");
            }

            if (run.Submissions.Count == 0)
                Console.WriteLine("  No submissions found under this run.");
        }

        Console.WriteLine();
        CheckErrRunResponseDto? errRun = null;
        try
        {
            errRun = await ros.CheckErrRunAsync(year.ToString(), errRunReference);
        }
        catch (RosClientException ex)
        {
            // Expected when no e-working days were reported that month - there's no ERR run to find.
            Console.WriteLine($"Couldn't check {errRunReference} (normal if no e-working allowance was paid that month): {ex.Message}");
        }

        if (errRun is not null)
        {
            Console.WriteLine($"{errRunReference} (Enhanced Reporting): {errRun.Status}");
            if (errRun.Amount is not null)
                Console.WriteLine($"  Run total {errRun.Amount:C}");
            foreach (var e in errRun.ValidationErrors ?? [])
                Console.WriteLine($"  ERROR {e.Code} {e.Path}: {e.Description}");

            foreach (var s in errRun.Submissions)
            {
                var detail = await ros.CheckErrSubmissionAsync(year.ToString(), errRunReference, s.SubmissionId);
                Console.WriteLine();
                Console.WriteLine($"  {detail.SubmissionId}: {detail.Status}");
                if (detail.Summary is { } sum)
                    Console.WriteLine($"    {sum.Count} item(s) - {sum.Amount:C}");
                foreach (var e in detail.ValidationErrors ?? [])
                    Console.WriteLine($"    ERROR {e.Code} {e.Path}: {e.Description}");
                foreach (var item in detail.InvalidItems ?? [])
                {
                    if (errRun.SavedItems.Any(x => x.LineItemId == item.LineItemId))
                    {
                        Console.WriteLine($"    Rejected item {item.LineItemId} - since resubmitted and saved, no action needed:");
                    }
                    else
                    {
                        anyInvalid = true;
                        Console.WriteLine($"    INVALID ITEM {item.LineItemId} (not saved by ROS):");
                    }
                    foreach (var e in item.Errors)
                        Console.WriteLine($"      {e.Code} {e.Path}: {e.Description}");
                }
                foreach (var w in detail.Warnings ?? [])
                foreach (var e in w.Warnings)
                    Console.WriteLine($"    Warning on {w.LineItemId}: {e.Code} {e.Path}: {e.Description}");
            }

            if (errRun.Submissions.Count == 0)
                Console.WriteLine("  No submissions found under this run.");
        }

        if (anyInvalid)
            Console.WriteLine("\nROS rejected at least one item above - rejected items aren't saved and need correcting and resubmitting.");
        return anyInvalid || payrollCheckFailed ? 1 : 0;
    }

    if (args.Contains("--dry-run"))
    {
        var year = DateTime.Today.Year;
        var payDateDry = DateOnly.FromDateTime(DateTime.Today);

        Console.WriteLine($"Fetching current RPN for {employee.FirstName} {employee.FamilyName} ({employeeId}) - tax year {year}, {rosConfig.Environment} environment...");

        RpnDetails rpnDry;
        try
        {
            rpnDry = await ros.LookupRpnAsync(year, employeeId);
        }
        catch (RosClientException ex)
        {
            Console.WriteLine($"Could not fetch RPN from ROS: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"RPN {rpnDry.RpnNumber} (issued {rpnDry.RpnIssueDate:yyyy-MM-dd}): yearly tax credits {rpnDry.YearlyTaxCredits:C}, USC status {rpnDry.UscStatus}");
        Console.WriteLine("Dry run - figures only, nothing is ever submitted or recorded. Change any value to see it recalculate.");

        var grossDry = employee.DefaultMonthlyGross;
        var pensionDry = employee.DefaultMonthlyPensionContribution;
        var eworkingDaysDry = employee.DefaultMonthlyEworkingDays;
        var benefitsInKindDry = employee.DefaultBenefitsInKind
            .Select(b => new BenefitInKindLine(b.Description, b.Amount, Enum.Parse<BikCategory>(b.Category)))
            .ToList();

        // Budget "what if" overrides - start as copies of the real RPN's bands/credits so they can be
        // edited freely (e.g. to try next January's announced Budget changes) without touching rpnDry
        // itself. Purely in-memory: these variables live only for this --dry-run call and are gone as
        // soon as it returns to the main menu, never written anywhere.
        var payeBandsOverride = rpnDry.TaxRates.ToList();
        var uscBandsOverride = rpnDry.UscRates.ToList();
        var taxCreditsOverride = rpnDry.YearlyTaxCredits;

        bool OverridesActive() =>
            !payeBandsOverride.SequenceEqual(rpnDry.TaxRates) ||
            !uscBandsOverride.SequenceEqual(rpnDry.UscRates) ||
            taxCreditsOverride != rpnDry.YearlyTaxCredits;

        PayslipResult RecalculateDry()
        {
            var eworkingAllowance = eworkingDaysDry * employee.EworkingDailyRate;
            var inputs = PayrollInputs.MonthlyFor(
                $"Payslip-{payDateDry:yyyy-MM}", employeeId, employee.FirstName, employee.FamilyName, payDateDry, grossDry, pensionDry,
                eworkingAllowance, benefitsInKindDry);
            var effectiveRpn = rpnDry with
            {
                TaxRates = payeBandsOverride,
                UscRates = uscBandsOverride,
                YearlyTaxCredits = taxCreditsOverride
            };
            // Deliberately keyed on payDateDry's year, not the "year" the RPN was fetched for: moving the
            // pay date into a different year (e.g. to try January's figures once new bands/credits are
            // known) should reset onto that year's own cumulative basis - real stored totals if any exist
            // for it, otherwise YearToDateTotals.Zero - rather than carrying this year's pay/tax-to-date
            // into the overridden bands and back-paying the whole year's worth of a credit change in one
            // go, which is only correct for an actual same-year, mid-year RPN change.
            return PayrollCalculator.Calculate(effectiveRpn, ytdStore.Get(payDateDry.Year), PrsiSettings.ClassS, inputs);
        }

        var dryResult = RecalculateDry();

        while (true)
        {
            if (OverridesActive())
                Console.WriteLine("*** Budget what-if overrides active - PAYE/USC bands and/or tax credits differ from the real RPN ***");
            PrintPayslip(dryResult);
            Console.WriteLine();
            Console.Write("[G]ross pay, [P]ension, [E]working days, [B]enefits in kind, [D]ate, [T]ax band/credit overrides, [Q]uit back to menu: ");
            var choice = (Console.ReadLine() ?? "").Trim().ToUpperInvariant();

            if (choice == "Q")
            {
                skipPause = true;
                return 0;
            }
            if (choice == "G")
            {
                Console.Write($"New gross pay [{grossDry:0.00}]: ");
                if (decimal.TryParse(Console.ReadLine(), out var g)) grossDry = g;
            }
            else if (choice == "P")
            {
                Console.Write($"New pension contribution [{pensionDry:0.00}]: ");
                if (decimal.TryParse(Console.ReadLine(), out var p)) pensionDry = p;
            }
            else if (choice == "E")
            {
                Console.Write($"New e-working days [{eworkingDaysDry}]: ");
                if (int.TryParse(Console.ReadLine(), out var e)) eworkingDaysDry = e;
            }
            else if (choice == "B")
            {
                EditBenefitsInKind(benefitsInKindDry);
            }
            else if (choice == "D")
            {
                Console.Write($"New pay date [{payDateDry:yyyy-MM-dd}]: ");
                if (DateOnly.TryParse(Console.ReadLine(), out var d))
                {
                    payDateDry = d;
                    if (payDateDry.Year != year)
                    {
                        var basisForYear = ytdStore.Get(payDateDry.Year);
                        Console.WriteLine(basisForYear == YearToDateTotals.Zero
                            ? $"Note: {payDateDry.Year} has no year-to-date totals recorded, so this is calculated as period {payDateDry.Month} of a fresh year (nothing deducted yet) - good for previewing a new year's first payslip under overridden bands/credits, e.g. after a Budget."
                            : $"Note: using {payDateDry.Year}'s recorded year-to-date totals as the cumulative basis for period {payDateDry.Month}.");
                    }
                }
            }
            else if (choice == "T")
            {
                EditTaxOverrides(payeBandsOverride, rpnDry.TaxRates, uscBandsOverride, rpnDry.UscRates, ref taxCreditsOverride, rpnDry.YearlyTaxCredits);
            }

            dryResult = RecalculateDry();
        }
    }

    if (args.Contains("--show-ytd"))
    {
        var year = DateTime.Today.Year;
        var current = ytdStore.Get(year);
        Console.WriteLine($"Locally tracked year-to-date totals for {year}:");
        Console.WriteLine($"  Pay for income tax to date:            {current.PayForIncomeTaxToDate:C}");
        Console.WriteLine($"  Income tax deducted to date:           {current.IncomeTaxDeductedToDate:C}");
        Console.WriteLine($"  Gross pay (USC & PRSI base) to date:   {current.PayForUscToDate:C}");
        Console.WriteLine($"  USC deducted to date:                  {current.UscDeductedToDate:C}");
        Console.WriteLine($"  PRSI deducted to date:                 {current.PrsiDeductedToDate:C}");
        return 0;
    }

    if (args.Contains("--seed-ytd"))
    {
        var year = DateTime.Today.Year;
        Console.WriteLine($"Enter year-to-date totals for {year} as of the last real payslip BEFORE the one you're about to run:");
        var seeded = new YearToDateTotals(
            PromptDecimal("Pay for income tax to date"),
            PromptDecimal("Income tax deducted to date"),
            PromptDecimal("Gross pay (USC & PRSI base, before pension) to date"),
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
        Console.WriteLine($"=== VAT Payable balance as of today (current period runs {currentPeriod.Start:dd/MM/yyyy} - {currentPeriod.End:dd/MM/yyyy}) ===");

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
            VatRoundingAdjustmentAccountKey = managerIoConfig.VatRoundingAdjustmentAccountKey,
            RevenuePayeeName = managerIoConfig.RevenuePayeeName
        });

        try
        {
            // The account's own running balance, not a period-scoped reconstruction - this needs no
            // classification of *why* any given line was posted (settling a past period's liability
            // reduces the balance exactly as validly as reclaiming VAT on a purchase does), so it's as
            // reliable as reading it straight off Manager.io's own "Liabilities" summary.
            var balance = await managerIoForSummary.GetVatPayableBalanceAsync(today);
            Console.WriteLine(balance.Balance >= 0
                ? $"Currently owed to Revenue: {balance.Balance,10:C} (running - period isn't closed, don't file this)"
                : $"Currently owed to you:     {-balance.Balance,10:C} (running - period isn't closed, don't file this)");
            if (balance.UnexpectedLines.Count > 0)
                Console.WriteLine($"Note: {balance.UnexpectedLines.Count} entries from other transaction types found on VAT Payable - not included above, see --vat-return closer to period end.");
        }
        catch (Exception ex) when (ex is HttpRequestException or ManagerIoClientException)
        {
            Console.WriteLine($"Could not reach Manager.io for the VAT position: {ex.Message}");
        }

        return 0;
    }

    if (args.Contains("--expenses-report"))
    {
        Console.WriteLine("Expenses report for your accountant - exports business-expense Payments recorded in Manager.io as a CSV.");
        Console.Write("Start date (yyyy-MM-dd): ");
        if (!DateOnly.TryParse(Console.ReadLine(), out var expensesStart))
        {
            Console.WriteLine("Not a valid date.");
            return 1;
        }
        Console.Write("End date (yyyy-MM-dd): ");
        if (!DateOnly.TryParse(Console.ReadLine(), out var expensesEnd))
        {
            Console.WriteLine("Not a valid date.");
            return 1;
        }

        using var managerIoForExpenses = new ManagerIoClient(new ManagerIoOptions
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
            VatRoundingAdjustmentAccountKey = managerIoConfig.VatRoundingAdjustmentAccountKey,
            RevenuePayeeName = managerIoConfig.RevenuePayeeName
        });

        List<ExpenseLine> expenses;
        try
        {
            expenses = await managerIoForExpenses.GetExpensesReportAsync(expensesStart, expensesEnd);
        }
        catch (Exception ex) when (ex is HttpRequestException or ManagerIoClientException)
        {
            Console.WriteLine($"Could not pull expenses from Manager.io: {ex.Message}");
            return 1;
        }

        if (expenses.Count == 0)
        {
            Console.WriteLine("No expense payments found in that period.");
            return 0;
        }

        Console.WriteLine($"Found {expenses.Count} expense payment(s) totalling {expenses.Sum(e => e.Total):C}.");

        var expensesDir = Path.Combine(dataDir, "expenses-reports");
        Directory.CreateDirectory(expensesDir);
        var csvPath = Path.Combine(expensesDir, $"Expenses-{expensesStart:yyyyMMdd}-{expensesEnd:yyyyMMdd}.csv");
        File.WriteAllText(csvPath, ExpensesReportCsvWriter.Build(expenses));
        Console.WriteLine($"CSV written to: {csvPath}");
        return 0;
    }

    if (args.Contains("--vat-history"))
    {
        var records = new VatFilingStore(Path.Combine(dataDir, "vat-filings.json")).GetAll();
        if (records.Count == 0)
        {
            Console.WriteLine("No VAT filings recorded yet.");
            return 0;
        }
        Console.WriteLine("Recorded VAT filings:");
        foreach (var r in records.OrderBy(r => r.PeriodStart))
            Console.WriteLine($"  {r.PeriodStart:dd/MM/yyyy} - {r.PeriodEnd:dd/MM/yyyy}: filed {r.FiledOn:dd/MM/yyyy}, " +
                               $"sales {r.RoundedSalesVat}, purchases {r.RoundedPurchasesVat}");
        return 0;
    }

    if (args.Contains("--vat-mark-filed"))
    {
        Console.WriteLine("Record a VAT period as filed without running the full --vat-return flow -");
        Console.WriteLine("use this to backfill history, or to record one filed some other way.");
        Console.Write("Period start date (yyyy-MM-dd, e.g. 2026-07-01): ");
        if (!DateOnly.TryParse(Console.ReadLine(), out var periodStart))
        {
            Console.WriteLine("Not a valid date.");
            return 1;
        }
        var manualPeriod = VatPeriod.Containing(periodStart);
        Console.Write($"Period is {manualPeriod.Start:dd/MM/yyyy} - {manualPeriod.End:dd/MM/yyyy} - filed on date (yyyy-MM-dd) [today]: ");
        var filedOnInput = (Console.ReadLine() ?? "").Trim();
        var filedOn = string.IsNullOrWhiteSpace(filedOnInput) || !DateOnly.TryParse(filedOnInput, out var parsedFiledOn)
            ? DateOnly.FromDateTime(DateTime.Today) : parsedFiledOn;

        new VatFilingStore(Path.Combine(dataDir, "vat-filings.json")).MarkFiled(new VatFilingRecord(
            manualPeriod.Start, manualPeriod.End, filedOn,
            (int)PromptDecimal("Sales VAT (T1, whole euro)"), (int)PromptDecimal("Purchases VAT (T2, whole euro)")));
        Console.WriteLine("Saved.");
        return 0;
    }

    if (args.Contains("--vat-return"))
    {
        var period = VatPeriod.MostRecentlyCompleted(DateOnly.FromDateTime(DateTime.Today));
        Console.WriteLine($"VAT period: {period.Start:dd/MM/yyyy} - {period.End:dd/MM/yyyy}");

        var vatFilingStore = new VatFilingStore(Path.Combine(dataDir, "vat-filings.json"));
        var alreadyFiled = vatFilingStore.GetAll().FirstOrDefault(f => f.PeriodStart == period.Start);
        if (alreadyFiled is not null)
        {
            Console.WriteLine($"This period was already recorded as filed on {alreadyFiled.FiledOn:dd/MM/yyyy} " +
                               $"(sales {alreadyFiled.RoundedSalesVat}, purchases {alreadyFiled.RoundedPurchasesVat}).");
            Console.Write("Type CONTINUE to run it again anyway (e.g. to redo a botched reconciliation), anything else to cancel: ");
            if (Console.ReadLine() != "CONTINUE")
            {
                Console.WriteLine("Cancelled.");
                return 0;
            }
        }

        var gaps = vatFilingStore.FindGaps(period.Start);
        if (gaps.Count > 0)
        {
            Console.WriteLine("WARNING: no filing recorded for the following completed period(s) - they may have been missed:");
            foreach (var gap in gaps) Console.WriteLine($"  {gap:MMM yyyy}");
            Console.WriteLine("This run will only handle the current period below; a missed one needs its own --vat-return run,");
            Console.WriteLine("or if it was actually filed some other way, record it with --vat-mark-filed.");
        }

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
            VatRoundingAdjustmentAccountKey = managerIoConfig.VatRoundingAdjustmentAccountKey,
            RevenuePayeeName = managerIoConfig.RevenuePayeeName
        });

        VatFigures figures;
        try
        {
            figures = await managerIoForVat.GetVatFiguresAsync(period.Start, period.End);

            // Cross-check the classified sales/purchases split against the account's actual balance
            // movement over the same dates. These are NOT expected to be equal whenever a settlement
            // payment to Revenue falls inside the period (the normal case, since ROS's filing deadline for
            // the prior period lands inside this one) - a settlement genuinely reduces the account's raw
            // balance just like a purchase would, so classifiedNet (which excludes it) legitimately
            // differs from actualMovement (which doesn't) by exactly the settlement amount. What should
            // never differ is (classifiedNet - actualMovement) vs. the settlement total itself - if those
            // two disagree, some line touching VAT Payable wasn't accounted for by sales, correctly-
            // classified purchases, or an identified settlement, meaning something was missed or
            // misclassified (e.g. a mistyped ManagerIo:RevenuePayeeName).
            var balanceBefore = await managerIoForVat.GetVatPayableBalanceAsync(period.Start.AddDays(-1));
            var balanceAfter = await managerIoForVat.GetVatPayableBalanceAsync(period.End);
            var classifiedNet = figures.SalesVat - figures.PurchasesVat;
            var actualMovement = balanceAfter.Balance - balanceBefore.Balance;
            var settlementTotal = figures.SettlementLines.Sum(l => l.Amount);
            if (Math.Abs((classifiedNet - actualMovement) - settlementTotal) > 0.01m)
            {
                Console.WriteLine();
                Console.WriteLine($"WARNING: the sales/purchases split below nets to {classifiedNet:C}, and {settlementTotal:C} of settlement payments were excluded from it, but VAT Payable's actual balance only moved by {actualMovement:C} over this period - those don't reconcile.");
                Console.WriteLine("This usually means a payment to Revenue wasn't recognised (check ManagerIo:RevenuePayeeName matches the payee used) or VAT Payable carried a balance into this period. Review the figures below manually before filing.");
            }
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

        if (figures.SettlementLines.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Excluded from purchases VAT above - payment(s) to {managerIoConfig.RevenuePayeeName} (settles a prior period's liability, not a purchase):");
            foreach (var l in figures.SettlementLines) Console.WriteLine($"  {l.Date:yyyy-MM-dd}  {l.Amount,10:C}");
        }

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
        Console.WriteLine("Upload this file to ROS (My Services -> Complete a Form Online / File a Return -> VAT3), then submit");
        Console.WriteLine("payment there - you'll be asked to choose a payment date on ROS's own payment screen.");
        Console.WriteLine("Note: ROS doesn't reliably debit on the date you specify there - check your bank statement in a");
        Console.WriteLine("few days and correct the date on this payment directly in Manager.io if it's actually different.");
        Console.Write("Enter the payment date you specified on ROS (yyyy-MM-dd) [today], or type 'cancel' to skip recording a payment: ");
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

            vatFilingStore.MarkFiled(new VatFilingRecord(
                period.Start, period.End, paymentDate, vatReturn.RoundedSalesVat, vatReturn.RoundedPurchasesVat));
        }
        catch (ManagerIoClientException ex)
        {
            Console.WriteLine($"Could not record the reconciling payment in Manager.io: {ex.Message}");
            return 1;
        }

        return 0;
    }

    // For when ROS rejected a payslip after the original run had already recorded it in Manager.io (and
    // sent the ERR report): submits to ROS and updates YTD only. Roll YTD back to before this payslip first.
    var resubmitOnly = args.Contains("--resubmit-payslip");
    if (resubmitOnly)
    {
        Console.WriteLine("RESUBMIT MODE: submits the payslip to ROS and updates YTD only - Manager.io and ERR are skipped.");
        Console.WriteLine("The YTD shown below must be the totals BEFORE this payslip. If the rejected run already added it,");
        Console.WriteLine("quit and fix that with 'Seed/correct year-to-date totals' first.");
        Console.WriteLine();
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
                       $"PAYE deducted {startingYtd.IncomeTaxDeductedToDate:C}, gross pay (USC & PRSI base) {startingYtd.PayForUscToDate:C}, USC deducted {startingYtd.UscDeductedToDate:C}");
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
            EditBenefitsInKind(benefitsInKind);
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

    // An acknowledgement only means ROS received it - payslips are validated asynchronously, and an invalid
    // one is silently not saved even when the submission ends up COMPLETED. Wait for the outcome before
    // recording anything locally or in Manager.io.
    Console.WriteLine("Waiting for ROS to process the submission...");
    CheckPayrollSubmissionResponseDto? outcome = null;
    for (var attempt = 0; attempt < 24; attempt++)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        try
        {
            outcome = await ros.CheckPayrollSubmissionAsync(taxYear.ToString(), payrollRunReference, submissionId);
        }
        catch (RosClientException)
        {
            continue; // not always queryable immediately after acknowledgement
        }
        if (!outcome.Status.Equals("PENDING", StringComparison.OrdinalIgnoreCase)
            && !outcome.Status.Equals("NOT_ACKNOWLEDGED", StringComparison.OrdinalIgnoreCase)) break;
    }

    var invalidPayslips = outcome?.InvalidPayslips ?? [];
    if (invalidPayslips.Count > 0)
    {
        Console.WriteLine("ROS REJECTED the payslip - it was not saved. Nothing has been recorded locally or in Manager.io.");
        foreach (var p in invalidPayslips)
        foreach (var e in p.Errors)
            Console.WriteLine($"  {p.LineItemId}: {e.Code} {e.Path}: {e.Description}");
        return 1;
    }
    if (outcome is null || outcome.Status.Equals("PENDING", StringComparison.OrdinalIgnoreCase)
                        || outcome.Status.Equals("NOT_ACKNOWLEDGED", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"ROS hasn't finished processing yet (status: {outcome?.Status ?? "unknown"}).");
        Console.Write("Record it locally and in Manager.io anyway? Check it later with 'Check payroll submission status'. [y/N]: ");
        if (!(Console.ReadLine() ?? "").Trim().Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Stopped - nothing recorded locally or in Manager.io. Check it later with 'Check payroll submission status':");
            Console.WriteLine("  - if ROS rejected it: fix the problem and run payroll again.");
            Console.WriteLine("  - if ROS accepted it: update YTD with 'Seed/correct year-to-date totals' and record the payslip in Manager.io by hand.");
            return 1;
        }
    }
    else
    {
        Console.WriteLine($"ROS processed the submission: {outcome.Status}");
        foreach (var w in outcome.PayslipWarnings ?? [])
        foreach (var e in w.Warnings)
            Console.WriteLine($"  Warning on {w.LineItemId}: {e.Code} {e.Path}: {e.Description}");
    }

    var ytdAfter = ytdStore.Get(taxYear).Add(result);
    ytdStore.Set(taxYear, ytdAfter);

    if (resubmitOnly)
    {
        Console.WriteLine("Year-to-date totals updated. Resubmit mode: Manager.io and the ERR report were left alone");
        Console.WriteLine("(they were already recorded by the original run).");
        Console.WriteLine();
        Console.WriteLine("Done.");
        return 0;
    }

    Console.WriteLine("Recording payslip and payments in Manager.io...");

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
        BenefitInKindDeductionItemKeys = managerIoConfig.BenefitInKindDeductionItemKeys,
        EworkingAllowanceAccountKey = managerIoConfig.EworkingAllowanceAccountKey,
        PayslipYtdCustomFieldKeys = managerIoConfig.PayslipYtdCustomFieldKeys,
        PayslipHeaderCustomFieldKeys = managerIoConfig.PayslipHeaderCustomFieldKeys
    });

    try
    {
        var payslipKey = await managerIo.CreatePayslipAsync(result, ytdAfter);
        Console.WriteLine($"Manager.io payslip created: {payslipKey}");

        var paymentKey = await managerIo.CreatePaymentAsync(result.Inputs.PayDate, result.NetPay, "Salary");
        Console.WriteLine($"Manager.io salary payment created: {paymentKey}");

        if (result.EworkingAllowance > 0m)
        {
            var eworkingPaymentKey = await managerIo.CreateEworkingAllowancePaymentAsync(
                result.Inputs.PayDate, result.EworkingAllowance, eworkingDays, $"{employee.FirstName} {employee.FamilyName}");
            Console.WriteLine($"Manager.io e-working allowance payment created: {eworkingPaymentKey}");
        }
    }
    catch (ManagerIoClientException ex)
    {
        Console.WriteLine($"ROS submission succeeded but Manager.io recording failed: {ex.Message}");
        Console.WriteLine("Check which of the payslip / salary payment / e-working payment above were created, and record the rest in Manager.io manually.");
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
}

static void EditBenefitsInKind(List<BenefitInKindLine> list)
{
    if (list.Count == 0)
        Console.WriteLine("No benefits in kind on this payslip.");
    else
        for (var i = 0; i < list.Count; i++)
            Console.WriteLine($"  [{i}] {list[i].Description}: {list[i].Amount:C} ({list[i].Category})");

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
            list.Add(new BenefitInKindLine(description, amount, category));
    }
    else if (int.TryParse(bikInput, out var bikIndex) && bikIndex >= 0 && bikIndex < list.Count)
    {
        Console.Write($"New amount for '{list[bikIndex].Description}' [{list[bikIndex].Amount:0.00}], or 'remove': ");
        var editInput = (Console.ReadLine() ?? "").Trim();
        if (editInput.Equals("remove", StringComparison.OrdinalIgnoreCase))
            list.RemoveAt(bikIndex);
        else if (decimal.TryParse(editInput, out var newAmount))
            list[bikIndex] = list[bikIndex] with { Amount = newAmount };
    }
}

static void EditTaxOverrides(
    List<RateBand> payeBands, IReadOnlyList<RateBand> originalPayeBands,
    List<RateBand> uscBands, IReadOnlyList<RateBand> originalUscBands,
    ref decimal yearlyTaxCredits, decimal originalYearlyTaxCredits)
{
    while (true)
    {
        Console.WriteLine();
        Console.WriteLine("=== Budget what-if overrides (in-memory only - lost when you leave the dry run) ===");
        Console.WriteLine("PAYE bands:");
        PrintRateBands(payeBands);
        Console.WriteLine("USC bands:");
        PrintRateBands(uscBands);
        Console.WriteLine($"Yearly tax credits: {yearlyTaxCredits:C} (RPN says {originalYearlyTaxCredits:C})");
        Console.WriteLine();
        Console.Write("[P]AYE bands, [U]SC bands, [C]redits, [R]eset to RPN values, [A]pply and back to dry run: ");
        var choice = (Console.ReadLine() ?? "").Trim().ToUpperInvariant();

        if (choice == "A") return;
        if (choice == "R")
        {
            payeBands.Clear();
            payeBands.AddRange(originalPayeBands);
            uscBands.Clear();
            uscBands.AddRange(originalUscBands);
            yearlyTaxCredits = originalYearlyTaxCredits;
            Console.WriteLine("Reset to the RPN's real values.");
        }
        else if (choice == "P")
        {
            EditRateBands(payeBands);
        }
        else if (choice == "U")
        {
            EditRateBands(uscBands);
        }
        else if (choice == "C")
        {
            Console.Write($"Adjust yearly tax credits by (e.g. +200 or -150) [currently {yearlyTaxCredits:0.00}]: ");
            if (decimal.TryParse(Console.ReadLine(), out var delta)) yearlyTaxCredits += delta;
        }
    }
}

static void PrintRateBands(List<RateBand> bands)
{
    if (bands.Count == 0)
    {
        Console.WriteLine("  (none)");
        return;
    }
    foreach (var b in bands.OrderBy(x => x.Index))
        Console.WriteLine($"  [{b.Index}] {b.RatePercent}% up to {(b.YearlyCutOff.HasValue ? b.YearlyCutOff.Value.ToString("C") : "no limit (top band)")}");
}

static void EditRateBands(List<RateBand> bands)
{
    PrintRateBands(bands);
    Console.Write("Enter index to edit, 'remove <index>', 'new', or blank to go back: ");
    var input = (Console.ReadLine() ?? "").Trim();
    if (input.Length == 0) return;

    if (input.Equals("new", StringComparison.OrdinalIgnoreCase))
    {
        var nextIndex = bands.Count == 0 ? 0 : bands.Max(b => b.Index) + 1;
        Console.Write("Rate %: ");
        decimal.TryParse(Console.ReadLine(), out var rate);
        Console.Write("Yearly cut-off (blank = no limit, i.e. this is the top band): ");
        var cutoffInput = (Console.ReadLine() ?? "").Trim();
        decimal? cutoff = decimal.TryParse(cutoffInput, out var c) ? c : null;
        bands.Add(new RateBand(nextIndex, rate, cutoff));
    }
    else if (input.StartsWith("remove", StringComparison.OrdinalIgnoreCase))
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[1], out var removeIndex))
        {
            var band = bands.FirstOrDefault(b => b.Index == removeIndex);
            if (band is not null) bands.Remove(band);
        }
    }
    else if (int.TryParse(input, out var index))
    {
        var pos = bands.FindIndex(b => b.Index == index);
        if (pos >= 0)
        {
            var band = bands[pos];
            Console.Write($"New rate % [{band.RatePercent}]: ");
            var rateInput = (Console.ReadLine() ?? "").Trim();
            var newRate = decimal.TryParse(rateInput, out var r) ? r : band.RatePercent;

            Console.Write($"New yearly cut-off [{(band.YearlyCutOff?.ToString("0.00") ?? "no limit")}], or 'none' for no limit: ");
            var cutoffInput = (Console.ReadLine() ?? "").Trim();
            var newCutoff = band.YearlyCutOff;
            if (cutoffInput.Equals("none", StringComparison.OrdinalIgnoreCase)) newCutoff = null;
            else if (decimal.TryParse(cutoffInput, out var c)) newCutoff = c;

            bands[pos] = band with { RatePercent = newRate, YearlyCutOff = newCutoff };
        }
    }
}

static string[]? PromptForMenuChoice()
{
    while (true)
    {
        Console.WriteLine();
        Console.WriteLine("=== Payroll ===");
        Console.WriteLine("1. Run payroll - review and submit this month's payslip");
        Console.WriteLine("2. Payroll dry run - calculate and show this month's figures, nothing submitted");
        Console.WriteLine("3. Generate VAT3 return");
        Console.WriteLine("4. Summary - year-to-date tax + current VAT position");
        Console.WriteLine("5. Show year-to-date totals");
        Console.WriteLine("6. Seed/correct year-to-date totals");
        Console.WriteLine("7. VAT filing history");
        Console.WriteLine("8. Mark a VAT period as filed manually");
        Console.WriteLine("9. List RPNs held by ROS");
        Console.WriteLine("10. Export expenses report (CSV) - for your accountant's year-end accounts");
        Console.WriteLine("11. Check payroll + ERR submission status on ROS");
        Console.WriteLine("12. Resubmit a payslip ROS rejected - ROS + YTD only, skips Manager.io/ERR");
        Console.WriteLine("0. Quit");
        Console.Write("Choose an option: ");

        switch ((Console.ReadLine() ?? "").Trim())
        {
            case "1": return [];
            case "2": return ["--dry-run"];
            case "3": return ["--vat-return"];
            case "4": return ["--summary"];
            case "5": return ["--show-ytd"];
            case "6": return ["--seed-ytd"];
            case "7": return ["--vat-history"];
            case "8": return ["--vat-mark-filed"];
            case "9": return ["--list-rpns"];
            case "10": return ["--expenses-report"];
            case "11": return ["--check-submission"];
            case "12": return ["--resubmit-payslip"];
            case "0": case "q": case "Q": return null;
            default: Console.WriteLine("Not a valid option, try again."); break;
        }
    }
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
    foreach (var b in r.BenefitsInKind)
        Console.WriteLine($"{b.Description,-23}{b.Amount,10:C} (notional {b.Category} BIK - taxed but not paid in cash)");
    Console.WriteLine($"Pension contribution:  {r.EmployeePensionContribution,10:C}");
    Console.WriteLine($"PAYE (Class {r.PrsiClass}, RPN {r.RpnNumber}): {r.IncomeTax,10:C}");
    Console.WriteLine($"USC:                   {r.Usc,10:C}");
    Console.WriteLine($"PRSI ({r.PrsiRatePercent}%):        {r.EmployeePrsi,10:C}");
    Console.WriteLine($"Net pay:               {r.NetPay,10:C}");
    Console.WriteLine($"Cumulative tax credits:{r.CumulativeTaxCredits,10:C} ({r.TaxBasis} basis)");
    Console.WriteLine($"Cumulative cut-off:    {r.CumulativeStandardRateCutOff,10:C}");
    if (r.EworkingAllowance > 0m)
        Console.WriteLine($"e-working allowance:   {r.EworkingAllowance,10:C} (tax-free, paid separately, reported to ROS via ERR)");
}

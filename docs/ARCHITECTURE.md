# Architecture

## Entry point: menu vs. flags

Nearly everything that used to be top-level script logic now lives in one local function,
`async Task<int> RunOnce(string[] args)` - it dispatches on `args`, a plain `string[]` of CLI flags
(`--summary`, `--vat-return`, etc.), each recognized flag its own
`if (args.Contains("--x")) { ...; return N; }` block, falling through to the default payroll flow if
nothing matches (including empty `args`). Inside `RunOnce`, `return N` just returns from that call, same
as it always returned from Main before this existed as a separate function - no behaviour change to any
individual action.

What sits outside `RunOnce` is what decides *how many times* to call it. If launched with real
command-line flags (`args.Length != 0` at true process start - a genuine terminal/scripted invocation),
the top level just does `return await RunOnce(args);` once and the process exits normally, exactly as it
always did. If launched with no arguments at all (Rider's Debug button, a double-clicked compiled exe -
neither lets you pass arguments), it instead loops: `PromptForMenuChoice` shows a numbered menu and
translates the choice into the equivalent `args` value (e.g. picking "3" produces `["--summary"]`), calls
`RunOnce` with it, and loops back to the menu again once that call returns - so the app stays open across
actions instead of exiting after one. The loop also wraps each `RunOnce` call in a try/catch, so an
unexpected exception in one action prints an error and returns to the menu rather than killing the whole
session. `0. Quit` is the only way out of that loop, via `Environment.Exit` inside `PromptForMenuChoice`.

There's still exactly one code path per action regardless of how it's reached - the menu doesn't
duplicate any dispatch logic, it just decides which `args` to call `RunOnce` with and whether to do it
again afterward.

## The end-to-end flow

Running `dotnet run` in `Payroll/` (or picking "Run payroll" from the menu) does this, in order:

1. Load config from `appsettings.json` + user-secrets, validate required fields are present.
2. `RosClient.LookupRpnAsync` — a real-time, read-only HTTPS call to ROS asking "what's the current
   RPN for this employee?" Returns tax credits, PAYE bands, USC bands, USC status.
3. Load locally-tracked year-to-date totals (`YearToDateStore`, see below) for the tax year.
4. `PayrollCalculator.Calculate` combines the RPN + YTD totals + this period's inputs (gross pay,
   pension, e-working days, health insurance BIK, pay date) into a `PayslipResult` — the full
   PAYE/USC/PRSI breakdown and net pay.
5. Review loop: print the payslip, let you edit any input, recalculate, repeat until you approve or quit.
6. On approve (with an extra typed `SUBMIT` confirmation if pointed at Production):
   a. `RosClient.CreatePayrollSubmissionAsync` — the actual submission to Revenue.
   b. Update the local YTD store with this payslip's figures.
   c. `ManagerIoClient.CreatePayslipAsync` + `CreatePaymentAsync` — record it in the books.
   d. If e-working days > 0, `RosClient.SubmitRemoteWorkingAllowanceAsync` — a separate Enhanced
      Reporting Requirements (ERR) submission.

Each of steps 6a–6d is a separate network call with its own try/catch. If one fails after an earlier
one succeeded, the error message says so explicitly — check what actually went through before retrying
(retrying a step that already succeeded, e.g. re-running the whole approval after Manager.io failed,
would create a duplicate ROS submission).

## Why there's a local `YearToDateStore` at all

This is the single most important thing to understand about this codebase, and it's not obvious from
Revenue's marketing material about RPNs.

You'd expect the RPN to tell you "here's how much this employee has been paid and taxed so far this
year" so you can carry on the cumulative calculation. **It doesn't**, for an ongoing employment.
Revenue's own RPN Data Items spec is explicit about this (see `PayForIncomeTaxToDate` / item 116):

> The total amount of pay for income tax to date for all **previous ceased employments** in the same
> tax year... **In the case of recommencements**, this will include previous pay... from that employer
> in the same tax year for previous periods of employment.

In other words: those fields only carry a running total over from a job that *ended* earlier in the
year (so a new employer can pick up the cumulative calculation correctly). For a single continuous
employment — this one — they are always zero, forever, no matter how many payslips you've run. Revenue
expects **your own payroll software** to track the running total, the same job BulletHQ was quietly
doing all along.

That's what `YearToDateStore` (in `Payroll/YearToDateStore.cs`) does: a small JSON file, keyed by tax
year, holding `{ PayForIncomeTaxToDate, IncomeTaxDeductedToDate, PayForUscToDate, UscDeductedToDate,
PrsiDeductedToDate }` (the last is informational only - PRSI isn't cumulative, see below - kept purely
so `--summary`/`--show-ytd` can report a running total). It's updated once per successful ROS payroll
submission (`Program.cs`, right after `CreatePayrollSubmissionAsync` succeeds), by adding that payslip's
figures via `YearToDateTotals.Add`.

It lives at `Storage:DataDirectory` from config if set, otherwise `Environment.SpecialFolder.
ApplicationData` (which resolves to `~/Library/Application Support` on macOS - **not** `~/.config`,
which is the Linux XDG convention people sometimes assume). Since a lost or dead machine would take this
file with it otherwise, `Storage:DataDirectory` exists specifically so it can point at a synced/backed-up
folder instead (this setup uses the same OneDrive folder the ROS cert lives in).

**If this file is ever lost, wrong, or you skip a month without updating it, every subsequent PAYE/USC
calculation will be wrong** (in the same direction we discovered on the first real run: it'll look like
you have far more unused tax credits than you actually do, and dramatically under-tax you). Use
`--show-ytd` to check it and `--seed-ytd` to fix it from a real payslip if it ever drifts — see
`docs/MAINTENANCE.md`.

## The cumulative PAYE/USC calculation

Irish PAYE on the "Cumulative" basis (as opposed to "Week 1") works out your tax for the *whole year to
date* on every payslip, then subtracts what's already been deducted. This is what makes credit or band
changes self-correct automatically: increase your annual tax credit mid-year, and the very next payslip
retroactively applies the full-year benefit of that change against everything already paid — see the
`PayrollCalculator.CalculateCumulativePeriodDeduction` method.

Mechanically, for income tax:

```
cumulativePay = yearToDate.PayForIncomeTaxToDate + thisPeriod.PayForIncomeTax
cumulativeCutOff(bandN) = bandN.YearlyCutOff * periodNumber / periodsInYear
cumulativeCredit = rpn.YearlyTaxCredits * periodNumber / periodsInYear
cumulativeTaxDue = tax(cumulativePay, scaled bands) - cumulativeCredit   (floored at 0)
thisPeriodTax = cumulativeTaxDue - yearToDate.IncomeTaxDeductedToDate
```

USC follows the identical shape (its own bands, no credit, no pension relief). PRSI is *not* cumulative
— it's a flat rate on this period's taxable gross pay only, because Revenue doesn't put PRSI rates on
the RPN at all (see below).

`PeriodNumber` is just the pay date's calendar month (1–12), assumed via `PayrollInputs.MonthlyFor` —
this only works because pay is monthly and aligned to the calendar tax year. If pay frequency or
alignment ever changes, this assumption needs revisiting.

## PRSI: not on the RPN, tracked by us

Revenue's RPN gives PAYE and USC bands, but never a PRSI rate — that's a DSP (Department of Social
Protection) responsibility, not Revenue's, and it isn't part of the RPN schema at all. `PrsiSettings.cs`
holds an effective-dated rate table for Class S (proprietary director):

```csharp
public static PrsiSettings ClassS => new()
{
    PrsiClass = "S",
    RateHistory =
    [
        new PrsiRatePeriod(new DateOnly(2025, 10, 1), 4.2m, 0m),
        new PrsiRatePeriod(new DateOnly(2026, 10, 1), 4.3m, 0m)
    ]
};
```

Class S has no employer PRSI contribution (that's the `0m`) and no lower-income threshold exemption the
way Class A has — it's a flat rate on all reckonable pay. **These numbers came from a web search
originally and were wrong** (4.1%/4.35% instead of the real 4.2%/4.3%) — they were only caught because
a real BulletHQ payslip's actual PRSI deduction didn't match. Don't trust a search result for a PRSI
rate; cross-check against a real payslip or gov.ie before updating this table. See
`docs/MAINTENANCE.md` for the annual update procedure.

## Benefits in Kind (BIK)

A non-cash benefit (employer-paid medical insurance, a company car, subsidised accommodation, a
preferential loan) inflates the PAYE/USC/PRSI taxable base exactly like cash pay would, but — since the
money never touches the employee's bank account — it must **not** increase net pay.

This is deliberately modelled as a **list**, not a single named field
(`PayrollInputs.BenefitsInKind: IReadOnlyList<BenefitInKindLine>`, each a `Description`, `Amount`, and
`BikCategory`), specifically so that adding a *second* kind of benefit is a config/data change, not a
code change — see `docs/MAINTENANCE.md`'s "Adding a new kind of Benefit in Kind" for the actual
procedure. `PayrollCalculator` just sums every line's `Amount` into `taxableGrossPay = GrossPay +
TotalBenefitInKind` and uses that for all three tax bases, while net pay is still calculated off the
real cash `GrossPay` only.

**Why only two categories** (`BikCategory.General` / `MedicalInsurance`), not one per benefit type: on
the ROS submission side, Revenue's Payroll Submission schema has exactly one benefit-specific field
beyond the generic bucket:

- `grossPay` includes the total BIK (per Revenue's own definition: "including notional pay").
- `taxableBenefits` is the generic bucket for non-cash benefits — Revenue's own field definition names
  "private use of a company car, free or subsidised accommodation, preferential loans" as the textbook
  example of what belongs here (PSR Data Items, item 47).
- `grossMedicalInsurance` is the *one* exception — specific to medical insurance, so Revenue can
  cross-check it against the employee's personal medical insurance relief credit claim (item 38). It's
  reported *in addition to* `taxableBenefits`, not instead of it.

So `RosClient.MapToPayslipSubmission` sums `TaxableBenefits` across *every* BIK line regardless of
category, and separately sums `GrossMedicalInsurance` from only the `MedicalInsurance`-category lines.
Two categories is the correct and complete model for this schema — it's not a placeholder for "more
categories later". A few other statutory categories in the same schema (share-based remuneration,
pension scheme contributions, termination lump sums) have their own dedicated fields entirely and
explicitly aren't part of the generic benefits bucket; they aren't Benefits in Kind in this sense and
this mechanism doesn't cover them — see `docs/MAINTENANCE.md`.

On the Manager.io side, each BIK line is recorded as an Earnings line (the notional value) offset by an
equal Deduction line, so it shows up on the payslip for visibility without changing the recorded net
pay. The Deduction line's account key comes from `ManagerIoOptions.BenefitInKindDeductionItemKeys`, a
dictionary keyed by the benefit's exact `Description` — a benefit whose description has no entry there
throws rather than silently dropping the offsetting deduction (`ManagerIoClient.CreatePayslipAsync`).

## The ROS REST API

Base docs: Revenue publishes the full PAYE Modernisation web service spec, with real JSON examples, at
<https://github.com/revenue-ie/paye-employers-documentation> (and a GitHub Pages mirror at
`https://revenue-ie.github.io/paye-employers-documentation/`). This is the primary source for anything
not covered here — the REST endpoint table, JSON schemas, and validation rules all live there. Look
under `PIT4/rest/` for the current REST guide and `PIT4/examples/` for real request/response JSON.

### Authentication: HTTP Signatures, not OAuth

There's no API key or OAuth flow. Every request is signed using the private key from your **ROS digital
certificate** (a `.p12` file), per the `draft-cavage-http-signatures-08` spec:

- `keyId` = Base64 of the certificate's raw DER bytes.
- `algorithm` = `rsa-sha512`.
- Signed headers: `(request-target)`, `host`, `date`, and (for POST) `digest` (SHA-512 hash of the body,
  base64, no algorithm prefix).
- Implementation: `Payroll.Ros/RosHttpSignatureHandler.cs`, a `DelegatingHandler` wired into the
  `RosClient`'s `HttpClient`.

### The P12 password quirk

The password that unlocks the `.p12` file is **not** the password you type into ROS. It's the MD5 hash
of that password (as Latin-1 bytes), Base64-encoded. This is documented in Revenue's REST Integration
Guide, Appendix A, and implemented in `RosCertificateLoader.DerivePkcs12Password`. If cert loading fails
with "the password may be incorrect," this derivation is the first thing to double check — verify
independently with `openssl pkcs12 -in file.p12 -noout -passin pass:<derived>` before assuming the code
is at fault (that's how a real password-mismatch bug was diagnosed this session, distinct from an
earlier red herring about macOS Keychain PKCS12 compatibility).

### Employment ID is Revenue's, not yours

`Employee:EmploymentId` in config is **not** something you choose — it's whatever Revenue assigned when
the employment was registered (in this case `2464`, discovered via `--list-rpns` after `"1"` returned no
results). If RPN lookups start failing with "no RPN found," check this first — run `--list-rpns` to see
what ROS actually has on file.

### Environments

`RosOptions.Environment`: `Pit` (`softwaretest.ros.ie`, mirrors production, needs a *separate*
registration via Revenue's PAYE Modernisation PIT Help Desk and its own cert — a normal production cert
will not authenticate there) or `Production` (`www.ros.ie`, real submissions, real consequences).
Config currently points at `Production` because a separate PIT cert was never obtained — every
submission this app makes is real. `Program.cs` requires typing `SUBMIT` before any Production
submission as a deliberate extra confirmation step.

## The Manager.io API

Manager.io's local API2 (served by the desktop app at `http://127.0.0.1:<port>/api2`) follows a
"-form" convention that isn't obvious from the endpoint names alone:

- Plain resource paths (`/employees`, `/payslips`, `/payments`, `/bank-and-cash-accounts`) are
  **read-only listings**.
- To create, update, or delete, use the corresponding `-form` path: `POST /payslip-form` to create,
  `GET/PUT/DELETE /payslip-form/{key}` to read/update/delete one.
- The full OpenAPI spec is served unauthenticated at `GET /api2` — useful for discovering what
  endpoints exist, but it does **not** describe the request/response body shapes for `-form` endpoints
  (they're typed as bare `object` in the spec). The only way to learn the real field names/casing is to
  `GET` an existing record's `-form/{key}` and copy its shape — which is what `Payroll.ManagerIo/Dto/`
  is built from. Field name casing is genuinely inconsistent (`Date`, `employee`, `Earnings` — note the
  lowercase `employee`) — this isn't a typo, it's copied verbatim from what the live API actually
  returns.
- There is no endpoint to *list* configured Payslip Deduction/Earnings Items — only create
  (`POST /payslip-deduction-item-form`) or read-by-key. But a payslip's `Deductions[].Item` doesn't
  actually require a "real" Payslip Deduction Item at all — a plain Chart of Accounts key (from
  `GET /chart-of-accounts`) works fine too (verified live). So the easiest way to find or create any key
  you need — deduction item or otherwise — is `GET /chart-of-accounts`, which lists every account with
  its key and needs no special permissions beyond the API key itself.
- Creating a record returns `201` with the full created object echoed back in the body, including an
  `id` field (also duplicated as `Key`) — there's no `Location` header. `ManagerIoClient.ExtractCreatedKey`
  reads `id` from the body, with a `Location`-header fallback kept only in case that ever changes.
- A payment line's `Amount` can be negative (verified live) — needed for the VAT reconciliation's
  rounding-adjustment line, which can go either direction.
- `DateOnly.ToString(format)` throws if the format string contains a time separator (`:`), even as
  literal text — so `"yyyy-MM-ddT00:00:00"` (the date shape Manager.io's `-form` endpoints expect)
  crashes. Build the `T00:00:00` suffix by string concatenation instead
  (`ManagerIoClient.ToManagerIoDate`). This shipped and went undetected for a while because the first
  round of live tests used raw `curl` with a hardcoded date string, not the actual C# code path — a
  reminder that testing the wire format isn't the same as testing the code that produces it.
- Authentication: `X-API-KEY` header, no OAuth.

## Enhanced Reporting Requirements (ERR)

Since 2024, certain non-taxable payments (currently: remote/e-working daily allowance, small benefit
exemption, travel & subsistence) must be reported to Revenue separately from the payroll submission,
via the ERR web services. This app only implements the remote-working-allowance category
(`RosClient.SubmitRemoteWorkingAllowanceAsync`) because that's the only one currently in use — see
`docs/MAINTENANCE.md` if a different category (e.g. small benefit exemption / a Christmas bonus voucher)
ever needs reporting; the pattern is the same, just a different `category`/`subCategory` value and a
different subset of required fields (see Revenue's `ERR - Enhanced Reporting Submission Request Data
Items.pdf` in the same GitHub docs repo).

## VAT3 return (`--vat-return`)

Deliberately has zero ROS integration — Revenue doesn't publish a REST API for VAT (unlike PAYE), and
manually uploading the file on ROS takes about a minute anyway. This command only automates the
tedious/error-prone part: computing the figures and generating the XML.

**Period detection** (`VatPeriod.MostRecentlyCompleted`): Irish VAT is bi-monthly (Jan-Feb, Mar-Apr,
May-Jun, Jul-Aug, Sep-Oct, Nov-Dec). The command always targets the most recently *completed* period
relative to today, never the in-progress one — so running it on 5 September still correctly targets
Jul-Aug, not Sep 1-5.

**Sourcing the figures** (`ManagerIoClient.GetVatFiguresAsync`): pulls directly from the "VAT Payable"
control account's real ledger lines via `/receipt-lines` (sales side → T1) and `/payment-lines`
(purchases side → T2), filtered to `account == "VAT Payable"` and the period's date range. This was a
deliberate choice over recomputing VAT from raw invoice amounts and tax codes, because Manager.io has a
business-level tax-inclusive/exclusive setting that isn't visible in the API response for a given
line — by reading the already-split "VAT Payable" ledger entries instead, Manager has already resolved
that ambiguity before ever posting the entry, so there's nothing left to get wrong.

This only covers Receipts and Payments because that's the entirety of how this business's real
transactions are recorded (verified against live data - zero Sales Invoices, zero Purchase Invoices).
Four other transaction types can *also* post to a control account like VAT Payable - Credit Notes,
Debit Notes, Expense Claims, and Journal Entries - but their direction (sales-like vs purchase-like)
isn't verified the way Receipts/Payments are, so the command checks all four for the period and
**warns** if anything appears there rather than silently including or excluding it. If this business
ever starts using Sales Invoices, Purchase Invoices, or any of those four, extend
`GetVatFiguresAsync` to cover them properly rather than trusting the warning path indefinitely.

**The rounding reconciliation**: ROS's VAT3 form only accepts whole euro for T1/T2 (`VatReturn.
RoundedSalesVat`/`RoundedPurchasesVat`), so the actual amount paid (`NetPayable`, computed from the
*rounded* figures - matching how ROS itself calculates what you owe) essentially never exactly equals
the true unrounded liability accrued in the books (`UnroundedNetLiability`). BulletHQ apparently never
handled this and just let the VAT Payable account drift by a few cents every period. Instead, the
reconciling payment (`CreateVatReconciliationPaymentAsync`) posts two lines: one clearing VAT Payable by
the exact unrounded amount (so it lands on precisely zero), and a second absorbing the difference
(`RoundingAdjustment`, which can be negative) against a dedicated "VAT Rounding Adjustment" P&L account,
so the rounding is visible in the books as its own small line rather than silently swallowed or left
drifting.

**Data completeness matters more than the code here.** The mechanism was verified working correctly
against live data, but the *first* real test (checking against a period BulletHQ had already filed)
failed - not from a bug, but because Manager.io's books only went back to late July, missing real
transactions the actual filed return included. Before trusting this command's output for a real filing,
sanity-check the printed sales/purchase breakdown against what you actually know was invoiced/spent that
period - the tool can only total what's actually recorded.

**Filing history and gap detection.** `VatPeriod.MostRecentlyCompleted` only ever answers "what's the
latest completed period as of today" - on its own it has no memory of what's actually been filed, so a
skipped period wouldn't be caught, just silently superseded by whatever's most recent next time you run
it. `VatFilingStore` (`Payroll/VatFilingStore.cs`, a JSON file alongside `year-to-date.json`, so it lives
wherever `Storage:DataDirectory` points) closes that gap: every successful `--vat-return` run records a
`VatFilingRecord` for the period it just reconciled, and on startup `--vat-return` calls
`VatFilingStore.FindGaps` - walking every bi-monthly period from the earliest filing on record up to (not
including) the current target, flagging any that aren't in the store - to warn about anything that looks
skipped. It also checks whether the *current* target period is already recorded as filed, prompting for
explicit confirmation before letting you re-run it (protects against accidentally double-booking the
reconciling payment). `--vat-mark-filed` exists for backfilling history (there's naturally none from
before this feature existed) or recording a period filed some other way, without going through the full
Manager.io figure-pulling flow.

## `--summary`

A read-only, at-a-glance check: year-to-date PAYE/USC/PRSI from the local `YearToDateStore`, plus the
*current, still-open* VAT period's running sales/purchases VAT position. The latter reuses
`ManagerIoClient.GetVatFiguresAsync` - the exact same method `--vat-return` uses - but for
`VatPeriod.Containing(today)` (the in-progress period) rather than `VatPeriod.MostRecentlyCompleted`
(the last closed one). Everything `--vat-return`'s "Data completeness" caveat says about only covering
Receipts/Payments applies here identically - this is a running total, not something to file, and it's
only as complete as what's actually been entered in Manager.io so far this period.

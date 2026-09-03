# Maintenance

Practical guide for keeping this running yourself: what needs touching when, and how to diagnose it
when something's wrong. Read `docs/ARCHITECTURE.md` first if you haven't — this assumes you know why
the YTD store exists and why PRSI rates are hardcoded.

## Monthly routine

```
cd Payroll
dotnet run
```

Review the numbers against what you expect (compare to last month, sanity-check against your bank
balance/company cash position). Edit anything that needs it, approve, type `SUBMIT`. That's it — ROS,
Manager.io, and (if e-working days > 0) the ERR submission all happen automatically, and the local YTD
store updates itself.

`dotnet run -- --summary` is a quick, read-only "how are things looking" check any time — year-to-date
PAYE/USC/PRSI, and the current VAT period's running position — without going anywhere near an actual
submission.

**If you skip a month** (no payslip run in a given calendar month), nothing breaks automatically, but
make sure the pay date and period-number assumption still hold — `PeriodNumber` is just the pay date's
month, so paying twice in one calendar month or skipping a month entirely will throw off the cumulative
math unless you think through what period number Revenue would expect. This hasn't been tested; treat
it as a manual sanity-check situation if it ever comes up.

## Bi-monthly VAT return

```
dotnet run -- --vat-return
```

Run this any time after a two-month VAT period closes (it always targets the most recently completed
period automatically — see `ARCHITECTURE.md`). It'll print the sales/purchases VAT breakdown line by
line, write the VAT3 XML file, and tell you the path. **Before uploading, sanity-check the printed
figures against what you actually know was invoiced and spent that period** — the tool can only total
what's recorded in Manager.io, and it has no way to know if something's missing (see the "data
completeness" note in `ARCHITECTURE.md` — this already happened once, when Manager.io's books didn't go
back far enough to cover a full period).

If it prints a **WARNING about unexpected line types** (Credit Notes, Debit Notes, Expense Claims, or
Journal Entries touching the VAT Payable account), those amounts are deliberately *not* included in the
totals — review them manually and adjust the figures yourself before filing, or extend
`GetVatFiguresAsync` if that transaction type becomes a regular thing.

Once you've actually uploaded the file and paid on ROS, come back and give it the real payment date —
it'll book a reconciling payment in Manager.io that clears the VAT Payable account to exactly zero
(handling ROS's whole-euro rounding properly, unlike BulletHQ). Typing `cancel` at that prompt skips
recording anything, if you're not ready yet or the figures didn't look right.

## Annual tasks (do these every January, before the first payslip of the new tax year)

1. **Nothing to bump in config.** Tax year isn't stored anywhere — `Program.cs` derives it from the pay
   date itself (`var taxYear = payDate.Year`), which defaults to today and is editable in the review
   loop. RPN lookups, ROS submissions, and which year's slot in the YTD store gets read/written all
   follow from that one value. This was a config field originally; it was removed because a forgotten
   manual bump every January was a needless annual failure point.
2. **Check the PRSI rate table** in `Payroll.Core/PrsiSettings.cs` (`PrsiSettings.ClassS`). Tax credits
   and USC/PAYE bands come live from the RPN every run — you never need to touch those. PRSI rates do
   **not** come from the RPN (see `ARCHITECTURE.md`) — they're a plain hardcoded table here, and it's
   your job to keep it current. Check gov.ie's PRSI Class S rates page or the current year's Employer's
   Guide to PAYE, and **verify against a real payslip once you have one**, don't just trust a web
   search — a search-sourced figure was wrong once already (see git history / the ARCHITECTURE.md note
   on this). Add a new `PrsiRatePeriod` entry rather than editing the existing ones, so past payslips'
   math stays reproducible if you ever need to recheck it.
3. **First payslip of the year**: the RPN's own `PayForIncomeTaxToDate` etc. will legitimately be zero
   (fresh tax year), and the local YTD store also starts fresh for the new tax year automatically —
   it's keyed by tax year already (`YearToDateStore`/`YearToDateTotals.Zero` is the default for a year with no
   entry), so no action needed there; just don't manually seed the new year with anything.

## When something in your tax situation changes mid-year

Claiming a new tax credit, changing pension contribution %, starting/stopping a benefit in kind — none
of these need a code change. The RPN reflects credit/band changes automatically on the next
`dotnet run`; other figures (gross pay, pension, e-working days, benefits in kind) are entered or
edited interactively each run, with `appsettings.json` only holding the *defaults*.

One thing worth internalizing: a mid-year **annual** figure change (e.g. a new tax credit) gets applied
as a lump-sum catch-up on the very next payslip, not smoothed in gradually — that's correct behaviour
of Revenue's cumulative system, not a bug. See `ARCHITECTURE.md`'s cumulative calculation section.

## Adding a new kind of Benefit in Kind

Benefits in Kind (health insurance, a company car, subsidised accommodation, anything non-cash that's
still taxable) are deliberately built so a new one is a **config/data change, not a code change** — see
`ARCHITECTURE.md`. To add one:

1. **Create the matching account in Manager.io** (Settings → Chart of Accounts, or a Payslip Deduction
   Item — either works, verified live; see `ARCHITECTURE.md`'s Manager.io section). Name it exactly what
   you'll call the benefit, e.g. "Company Car (BIK)".
2. **Add its key to `appsettings.json`**, under `ManagerIo:BenefitInKindDeductionItemKeys`, keyed by that
   *exact* description string:
   ```json
   "BenefitInKindDeductionItemKeys": {
     "Health Insurance (BIK)": "...",
     "Company Car (BIK)": "the-new-key"
   }
   ```
3. **Enter the amount** — either interactively at the review screen (`[B]enefits in kind` → `new`), or if
   it recurs every month, add it to `Employee:DefaultBenefitsInKind` in `appsettings.json` so it's
   pre-filled each run:
   ```json
   { "Description": "Company Car (BIK)", "Amount": 250.00, "Category": "General" }
   ```
   `Category` must be `"MedicalInsurance"` (gets the extra ROS `grossMedicalInsurance` field, for the
   personal relief credit cross-check) or `"General"` (everything else non-cash — a car, accommodation,
   a loan — see Revenue's own field definition quoted in `ARCHITECTURE.md`). If what you're adding needs
   an entirely different ROS field (share-based remuneration, a pension scheme contribution, a lump
   sum) — those are genuinely NOT Benefits in Kind in this schema and this mechanism doesn't cover them;
   that would be a real code change to `RosClient`'s submission mapping.

That's the whole procedure — no C# needed for the common case.

## When your ROS certificate is renewed

ROS certs don't last forever — you'll need to renew via ROS at some point (there's no fixed schedule
tracked here; it just happens when Revenue tells you to). When it does:

1. Save the new `.p12` file somewhere sensible and update `Ros:P12Path` in `appsettings.json` to point
   to it.
2. `dotnet user-secrets set "Ros:P12PlainPassword" "..." --project Payroll` with the password for the
   *new* file.

That's genuinely the whole procedure — no code change. Every run loads the cert fresh from those two
config values (`RosClient`'s constructor calls `RosCertificateLoader.Load(options.P12Path,
options.P12PlainPassword)`), and the HTTP Signature's `keyId` and signing key are both derived from
whatever certificate that loads, at request time. Nothing else in the codebase caches or hardcodes
anything about a specific certificate.

The one thing worth remembering: **the password for a given `.p12` isn't always your everyday ROS login
password** — some renewal flows show a distinct password specific to that download. Verify independently
before assuming the app is broken:

```
openssl pkcs12 -in /path/to/new-cert.p12 -noout -passin pass:$(python3 -c "
import hashlib, base64
h = hashlib.md5('your-plain-password'.encode('latin-1')).digest()
print(base64.b64encode(h).decode())
")
```
`MAC verified OK` confirms it before you even touch the app; `Mac verify error: invalid password?` means
you've got the wrong plain password for that specific file.

## Cleaning up after a mistake

Deliberate design choice: this app never tries to correct or amend a submission automatically. If a
run goes out wrong — bad figures, duplicate submission, anything — fix it directly on ROS/Manager.io's
own websites, then clean up locally by hand using the checklist below. Don't just re-run the app for
the same period to "fix" it.

**Why not just resubmit via the app**: Revenue's real correction mechanism uses a *new* `lineItemID`
plus a `previousLineItemID` field pointing at the submission being superseded (verified against
Revenue's own correction example - see `ARCHITECTURE.md`). This app doesn't implement that - it always
generates the same `lineItemID` for a given month (`Payslip-{yyyy-MM}`) and never sets
`previousLineItemID`. Resubmitting the same `lineItemID` with no correction linkage isn't the documented
pattern, so what ROS would actually do with it is unverified - don't rely on it.

**What to check/fix by hand, depending on what the mistake touched:**

1. **The actual submission on ROS** - fix or delete it directly on the ROS website. This is the one
   that actually matters legally; everything else below is just keeping this app's own local records
   consistent with whatever ROS ends up with.
2. **Local year-to-date store** (`dotnet run -- --seed-ytd`, or edit `year-to-date.json` directly - it's
   at `Storage:DataDirectory` from config, or `~/Library/Application Support/Payroll` if that's not
   set) - set it to the *correct* cumulative totals as of the fixed state, not the sum of every attempt.
   `Get`/`Set` always start from whatever's in the file, so a wrong intermediate value just sits there
   silently until you overwrite it - there's no automatic reconciliation against what ROS actually has
   (see `ARCHITECTURE.md` on why the RPN can't tell you this either).
3. **Manager.io's payslip/payment records** - if the wrong run also created a payslip and/or payment via
   this app, correct or delete those directly in Manager.io. Nothing here does this automatically, and
   if the wrong figures already fed into a VAT period (health insurance BIK, or anything downstream),
   double-check that VAT return too.
4. **ERR submission**, if e-working allowance was part of the mistaken run - check ROS's ERR submission
   history for whether it needs its own correction. Unverified whether/how that works through the ROS
   portal itself (only the API's `previousLineItemID`-equivalent mechanism is confirmed) - treat it as
   its own thing to check, not something fixing the payroll side also resolves.

RPN state never needs touching - it's fetched fresh from ROS every run, nothing about it is cached
locally.

## Troubleshooting

**"The certificate data cannot be read with the provided password, the password may be incorrect."**
Check the P12 password derivation is being applied — this is the MD5-hash-then-Base64 quirk in
`RosCertificateLoader`, not your plain ROS password. Verify independently before assuming a code bug:

```
export ROS_PLAIN='your-plain-password'
DERIVED=$(python3 -c "
import hashlib, base64, os
h = hashlib.md5(os.environ['ROS_PLAIN'].encode('latin-1')).digest()
print(base64.b64encode(h).decode())
")
openssl pkcs12 -in /path/to/cert.p12 -noout -passin pass:"$DERIVED"
```
`MAC verified OK` means the password's right and any remaining error is a real code/environment issue.
`Mac verify error: invalid password?` means the plain password itself is wrong (mistyped, or it's not
the password associated with that specific `.p12` file — some cert renewals issue a distinct password
just for the file, separate from your everyday ROS login password).

**"No RPN found for {employeeId}..."**
`Employee:EmploymentId` in config doesn't match what Revenue actually has on file. Run
`dotnet run -- --list-rpns` to see the real employment ID(s) ROS holds for this employer/PPSN/tax year,
and fix `appsettings.json` accordingly. This isn't something you choose — Revenue assigns it.

**PAYE/USC come out far lower than expected (e.g. near zero) for no apparent reason**
Almost certainly the local YTD store (`--show-ytd`) doesn't reflect reality — either it was never seeded
correctly for the current tax year, or a submission's YTD update was somehow missed (e.g. the app
crashed between the ROS submission succeeding and the YTD store being updated — check ROS directly, via
ROS's own "PAYE Employers" self-service dashboard or a `CheckPayrollRunAsync` call, for what was
actually submitted, then `--seed-ytd` with the correct cumulative totals).

**A specific deduction/tax figure doesn't match a real payslip you're cross-checking against**
This is exactly how the PRSI rate error was caught originally — trust a real, already-filed payslip's
actual numbers over any documentation or web search. Work backwards from the real deducted amount
(`realAmount / taxableBase = actual rate`) and compare to what's hardcoded.

**Manager.io: "ManagerIo:BenefitInKindDeductionItemKeys has no entry for it"**
You've added a Benefit in Kind at the review screen (or in `Employee:DefaultBenefitsInKind`) whose
`Description` doesn't have a matching entry in `ManagerIo:BenefitInKindDeductionItemKeys`. This is the
whole point of that error — it means a new benefit's Manager.io side isn't wired up yet. See "Adding a
new kind of Benefit in Kind" below for the full (no-code) procedure.

**ROS rejects a submission with a validation error**
The error message includes Revenue's own `code`/`path`/`description` for each failed line item
(`RosClientException` message, built from `AcknowledgementResponseDto.Errors` — see `RosClient.cs`).
Cross-check the field named in `path` against the real schema — Revenue's own
`Employer_Submission_RPN_Validation_Rules.xlsx` (in the same GitHub docs repo, under
`PIT4/validation-rules/`) lists every validation rule if the error message alone isn't enough.

**Something failed partway through the approve flow**
The four steps (ROS payroll submission → local YTD update → Manager.io payslip+payment →
ERR submission) are sequential, each with its own error handling — read the console output carefully to
see exactly which step failed and which ones already succeeded before retrying anything, to avoid
double-submitting to ROS or double-recording in Manager.io.

## Known gaps / things not built

These weren't needed yet, so they weren't built — don't assume they're handled:

- **Correction submissions.** If a real submission to Revenue turns out wrong, this app has no way to
  submit a correction — you'd currently have to do that manually via ROS's own website, or extend
  `RosClient` (Revenue's PSR schema supports corrections by resubmitting with the same `lineItemID`).
- **New employees / New RPN.** `RosClient` only implements *looking up* an existing RPN. If this ever
  needs to run payroll for a second employee, you'd need the "New RPN" web service
  (`createNewRPN` in Revenue's REST guide) to register them first.
- **Other ERR categories.** Only `REMOTE_WORKING_DAILY_ALLOWANCE` is implemented. Small Benefit
  Exemption (e.g. a tax-free voucher) or Travel & Subsistence would need a near-identical new method on
  `RosClient` with a different `category`/`subCategory`.
- **Skipped/irregular pay periods.** The `PeriodNumber = pay date's month` assumption (see
  `ARCHITECTURE.md`) hasn't been stress-tested against a missed month or an extra mid-month payment.
- **Non-Class-S PRSI.** Only Class S is modelled. Irrelevant unless the company ever has a non-director
  employee.
- **VAT via Sales/Purchase Invoices, Credit Notes, Debit Notes, Expense Claims, or Journal Entries.**
  `--vat-return` only totals Receipts and Payments (see `ARCHITECTURE.md`) because that's the only way
  this business's transactions are currently recorded. It warns if it finds VAT Payable activity from
  the other four transaction types, but doesn't include them in the total - if Sales Invoices or any of
  those start being used regularly, `GetVatFiguresAsync` needs extending to include them properly.

## Where to look things up

Revenue's own PAYE Modernisation documentation, with real JSON/XML examples for every scenario, lives at
<https://github.com/revenue-ie/paye-employers-documentation> (mirror served at
<https://revenue-ie.github.io/paye-employers-documentation/>). Everything this codebase's ROS
integration is built on came from there — the REST Web Service Integration Guide, the RPN/Payroll
Submission/ERR Data Items PDFs, and the `PIT4/examples/` and `PIT4/Scenarios/` folders of real
request/response JSON. If ROS ever changes behaviour or a new field needs mapping, that repo is the
first place to check — search it (`PIT4/Scenarios/JSON/` has dozens of worked examples covering
corrections, leavers, multiple employments, etc.) before guessing at a schema.

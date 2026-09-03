# Payroll

A personal payroll tool for RhysCom Ltd: fetches the current RPN from ROS, calculates PAYE/USC/PRSI
for a proprietary director (Class S), lets you review and edit before approving, then submits to
Revenue and records the payslip + payment in Manager.io.

Replaces BulletHQ. Built for one employee (Rhys Waters) — it is not a general-purpose payroll product.

## Projects

| Project | Purpose |
|---|---|
| `Payroll.Core` | Domain model + the PAYE/USC/PRSI calculator. No I/O, fully unit tested. |
| `Payroll.Ros` | Client for Revenue's PAYE Modernisation REST API (RPN lookup, Payroll Submission, ERR). |
| `Payroll.ManagerIo` | Client for Manager.io's local API2 (creates the payslip + payment records). |
| `Payroll` | The CLI — wires the above together, holds config and local state. |
| `Payroll.Core.Tests` | xunit tests for the calculator. |

See `docs/ARCHITECTURE.md` for how the pieces fit together and `docs/MAINTENANCE.md` for what to do
when tax rates change, something breaks, a new tax year starts, or you need to clean up after a mistake
(this app never auto-corrects a submission — fix it on ROS/Manager.io directly, then sync local state
by hand, see `docs/MAINTENANCE.md`).

## Running it

Launched with no arguments — Rider's Debug button, double-clicking the compiled `.exe`, or plain
`dotnet run` from `Payroll/` — it shows a numbered menu instead of guessing what you want:

```
=== Payroll ===
1. Run payroll - review and submit this month's payslip
2. Generate VAT3 return
3. Summary - year-to-date tax + current VAT position
4. Show year-to-date totals
5. Seed/correct year-to-date totals
6. VAT filing history
7. Mark a VAT period as filed manually
8. List RPNs held by ROS
0. Quit
```

Each option is equivalent to one of the command-line flags below — the menu just picks one for you
interactively (`PromptForMenuChoice` in `Program.cs`) rather than requiring you to remember flag names.
Passing a flag directly (e.g. `dotnet run -- --summary`) skips the menu entirely, for terminal use.

**Option 1 / plain `dotnet run`** fetches the current RPN (read-only), shows a review screen, and lets
you edit gross pay, pension, e-working days, benefits in kind, or the pay date before approving.
Approving in the Production environment requires typing `SUBMIT` to confirm — it's a real submission to
Revenue at that point. Adding a *new kind* of benefit in kind (beyond health insurance) is a config
change, not a code change — see `docs/MAINTENANCE.md`.

Diagnostic commands (none of these submit anything):

```
dotnet run -- --list-rpns    # lists every RPN ROS holds for this employer/tax year
dotnet run -- --show-ytd     # shows the locally tracked year-to-date totals
dotnet run -- --seed-ytd     # overwrites the locally tracked year-to-date totals (interactive prompts)
dotnet run -- --summary      # at-a-glance: YTD PAYE/USC/PRSI, plus the current (still-open) VAT period's running position
```

Bi-monthly VAT3 return (no ROS integration — you upload the file yourself):

```
dotnet run -- --vat-return
```

Auto-detects the most recently completed two-month VAT period, pulls sales/purchases VAT straight from
Manager.io's "VAT Payable" ledger, writes the VAT3 XML file for you to upload to ROS manually, then —
once you confirm you've actually paid — books a reconciling payment in Manager.io that clears VAT
Payable to exactly zero (including the cents ROS's whole-euro rounding leaves behind, which BulletHQ
never handled). See `docs/ARCHITECTURE.md` for how the figures are sourced.

It also keeps a filing history (`vat-filings.json`, alongside `year-to-date.json`) so it can warn you if
a completed period was never filed rather than silently moving on to the next one:

```
dotnet run -- --vat-history     # lists every period recorded as filed
dotnet run -- --vat-mark-filed  # records a period as filed without running the full flow (backfill/correction)
```

## Configuration

Non-secret values live in `Payroll/appsettings.json` (employer registration number, PPSN, default
monthly figures, Manager.io record keys, etc.) — see `Payroll/appsettings.example.json` for the
structure with placeholder values (the real file is gitignored, since it holds personal data).

`Storage:DataDirectory` controls where the year-to-date store and generated VAT3 XML files live —
default is `~/Library/Application Support/Payroll`, but pointing it at a synced/backed-up folder (this
setup uses OneDrive, alongside the ROS cert) means that data survives a lost or dead machine.

Secrets are never stored in `appsettings.json` or committed anywhere — they live in .NET user-secrets,
local to this machine:

```
dotnet user-secrets set "Ros:P12PlainPassword" "..." --project Payroll
dotnet user-secrets set "ManagerIo:ApiKey" "..." --project Payroll
```

To see what's set (without revealing values): `dotnet user-secrets list --project Payroll`.

## Testing

```
dotnet test
```

Covers the cumulative PAYE/USC calculation (including a full-year invariant check) and the Benefit in
Kind handling. It does not and cannot test the ROS/Manager.io clients against real services — those are
validated by running the app for real.

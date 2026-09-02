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

```
cd Payroll
dotnet run
```

This fetches the current RPN (read-only), shows a review screen, and lets you edit gross pay, pension,
e-working days, benefits in kind, or the pay date before approving. Approving in the Production
environment requires typing `SUBMIT` to confirm — it's a real submission to Revenue at that point.
Adding a *new kind* of benefit in kind (beyond health insurance) is a config change, not a code change
— see `docs/MAINTENANCE.md`.

Diagnostic commands (none of these submit anything):

```
dotnet run -- --list-rpns    # lists every RPN ROS holds for this employer/tax year
dotnet run -- --show-ytd     # shows the locally tracked year-to-date totals
dotnet run -- --seed-ytd     # overwrites the locally tracked year-to-date totals (interactive prompts)
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

## Configuration

Non-secret values live in `Payroll/appsettings.json` (employer registration number, PPSN, default
monthly figures, Manager.io record keys, etc.).

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
# Payroll

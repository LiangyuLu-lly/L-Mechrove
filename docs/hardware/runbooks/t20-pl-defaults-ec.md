# T20 first real-machine runbook — per-SKU PL / Tcc defaults read from the EC

Task: `feat(modes): source per-SKU PL defaults from EC, fail-closed`.

Vendor source (`GCUService.decompiled.cs:65047-65096`):
Gaming PL `1840-1843`, Office PL `1844-1847`, **Turbo** PL `1959-1962`
(`GetTurboPLDefaultValue` — *not* BatterySaver), per-mode Tcc default offsets `2008` (Gaming) /
`2009` (Office) / `2010` (Turbo).

The read chain, the fail-closed branch and the service wiring are proven under fakes. The
**numeric per-SKU values** are hardware-resident and cannot be unit-tested — that half is the
BLOCKED-HW part, recorded in `.omo/evidence/f3-generation-status.json`.

Run in **Windows PowerShell 5.1** on the target machine.

---

## Step 0 — Confirm the read channel exists

```powershell
Test-Path '\\.\ACPIDriver'
Get-Service -Name '*ACPIDriver*' -ErrorAction SilentlyContinue | Select-Object Name, Status
```

Expected: `True` and a running driver service. If either is false/absent, EC defaults are
unavailable on this machine — the correct T20 behaviour is **PL editing disabled** (step 4),
not guessed values. Record this as BLOCKED-HW for that machine.

## Step 1 — Read the raw EC defaults (read-only)

```powershell
dotnet run --project src\Probe\Probe.csproj -- ec-read 1840,1841,1842,1843,1844,1845,1846,1847,1959,1960,1961,1962,2008,2009,2010
```

Expected: 14 byte values. Map them as Gaming PL1/PL2/PL4 = `1840/1841/1842`, Office =
`1844/1845/1846`, Turbo = `1959/1960/1961`, and the Tcc defaults = `2008/2009/2010`.
Record the raw output + SHA256 into `.omo\evidence\f3-generation-status.json`.

Sanity: Turbo values must come from `1959-1962`. If they are identical to Office and the
console shows a distinct Turbo power limit, re-check the addresses before trusting the run.

## Step 2 — Confirm the app reads the same values

Start the app and open the mode/power editor. Then:

```powershell
Select-String -LiteralPath "$env:AppData\MechrevoLite\log.txt" -Pattern 'EC defaults read|disabling PL editing' |
  Select-Object -Last 5 | ForEach-Object { $_.Line }
```

Expected: for Gaming/Office/Turbo an `EC defaults read for <mode>` line appears, and the editor
shows the same numbers as step 1. `PL` values are sent as **strings**, `CpuTccOffset` as
`TjMax − offset`, and `PL4` is halved only when the double-flag is set (covered by unit tests).

## Step 3 — Confirm Turbo is labelled Turbo, not BatterySaver

```powershell
Select-String -LiteralPath "$env:AppData\MechrevoLite\log.txt" -Pattern 'Turbo|BatterySaver' |
  Select-Object -Last 10 | ForEach-Object { $_.Line }
```

Expected: the third default set is labelled **Turbo**; no `BatterySaver` label is attached to
the `1959-1962` family.

## Step 4 — Fail-closed check (do this on a machine without the EC channel, or by
temporarily denying the device)

```powershell
Select-String -LiteralPath "$env:AppData\MechrevoLite\log.txt" -Pattern 'EC read transport unavailable|EC .* unreadable' |
  Select-Object -Last 3 | ForEach-Object { $_.Line }
```

Expected: a log line saying the EC transport is unavailable / an address is unreadable, and the
mode's power-limit editor is **disabled**. No wattage number is invented.

## Step 5 — Confirm the only EC write is still the charge limit

```powershell
Select-String -LiteralPath "$env:AppData\MechrevoLite\log.txt" -Pattern 'ECWRITE|0x9C40A48C' | Measure-Object |
  Select-Object -ExpandProperty Count
```

Expected: `0`. T20 is a **read-only** consumer of `IEcReadTransport`; no new EC write path.

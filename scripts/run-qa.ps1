<#
  run-qa.ps1 - QA evidence binder (T31/Wave A0).

  Usage (the plan's QA line, verbatim; this box has no pwsh, so Windows PowerShell 5.1):
    powershell -NoProfile -File scripts\run-qa.ps1 -Id t31-h -Command "dotnet test tests\MechrevoLite.Tests\MechrevoLite.Tests.csproj -c Debug --settings tests\MechrevoLite.Tests\xunit.runsettings --filter FullyQualifiedName~TransportSeam --logger \"trx;LogFileName=t31-h.trx\""

  Hard assertions (any one of them failing = FAIL; the exit code alone is never trusted):
    * exit code == 0;
    * the .trx exists and parses; Skipped > 0 or Passed == 0 (filter matched no case) = FAIL;
    * `git rev-parse HEAD` is non-empty and `git status --porcelain` is empty BEFORE the run;
    * the written evidence file must carry command / exit_code / trx_sha256 / git_head /
      script_sha256 bindings, and both hashes must equal the recomputed values.

  Artifacts:
    .omo\evidence\<id>.txt      command / exit code / UTC / HEAD / status / trx counters / SHA256
    .omo\evidence\<id>.sha256   SHA256 of the artifact and of the evidence file itself
    .omo\evidence\run-qa.sha256 SHA256 of this script (refreshed on every run)

  ASCII only on purpose: config.encoding on this box is GBK, and PowerShell 5.1 reads
  BOM-less scripts as ANSI - non-ASCII comments corrupt statement boundaries.
#>
param(
    [Parameter(Mandatory = $true)][string]$Id,
    [Parameter(Mandatory = $true)][string]$Command
)

$ErrorActionPreference = 'Stop'

if ($Id -notmatch '^[A-Za-z0-9._-]+$') { throw "bad -Id '$Id' (want [A-Za-z0-9._-]+; it becomes a file name)" }

$repoRoot = Split-Path -Parent $PSScriptRoot
$evidenceDir = Join-Path $repoRoot '.omo\evidence'
New-Item -ItemType Directory -Force -Path $evidenceDir | Out-Null

$utf8 = New-Object System.Text.UTF8Encoding($false)
$evidencePath = Join-Path $evidenceDir "$Id.txt"
$hashPath = Join-Path $evidenceDir "$Id.sha256"

$scriptPath = $MyInvocation.MyCommand.Path
$scriptSha = (Get-FileHash -LiteralPath $scriptPath -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText((Join-Path $evidenceDir 'run-qa.sha256'), "$scriptSha  scripts\run-qa.ps1`n", $utf8)

$head = (& git -C $repoRoot rev-parse HEAD 2>&1 | Out-String).Trim()
$status = (& git -C $repoRoot status --porcelain 2>&1 | Out-String).Trim()

$problems = New-Object System.Collections.Generic.List[string]
if ([string]::IsNullOrWhiteSpace($head)) { $problems.Add('git rev-parse HEAD is empty') }
if ($status.Length -ne 0) { $problems.Add("git status --porcelain is not clean: $status") }

$exitCode = -1
$trxPath = ''
$trxSha = ''
$counts = @{ total = -1; executed = -1; passed = -1; failed = -1; skipped = -1 }
$startedUtc = (Get-Date).ToUniversalTime()

if ($problems.Count -eq 0) {
    Write-Host ">> run-qa [$Id] $Command"
    # A failing test writes to stderr; merging stderr into the pipeline under $ErrorActionPreference
    # 'Stop' turns that into a terminating error BEFORE the evidence file is written. Run the native
    # command with the preference relaxed (and catch a real throw) so a failure still leaves evidence.
    $nativeEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        Invoke-Expression $Command *>&1 | Out-String | Write-Host
        $exitCode = $LASTEXITCODE
    }
    catch {
        $problems.Add("test command threw: $($_.Exception.Message)")
        $exitCode = -1
    }
    finally {
        $ErrorActionPreference = $nativeEap
    }

    $trxRoot = Join-Path $repoRoot 'tests\MechrevoLite.Tests\TestResults'
    if ($Command -match '--results-directory\s+"?([^"\s]+)"?') {
        $trxRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Matches[1]))
    }
    if (Test-Path -LiteralPath $trxRoot) {
        $trx = Get-ChildItem -LiteralPath $trxRoot -Recurse -Filter "$Id.trx" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($null -ne $trx) { $trxPath = $trx.FullName }
    }

    if ($exitCode -ne 0) { $problems.Add("test command exit code $exitCode") }
    if ($trxPath.Length -eq 0) {
        $problems.Add("no $Id.trx under $trxRoot")
    }
    else {
        [xml]$trxXml = Get-Content -LiteralPath $trxPath -Raw
        $counters = $trxXml.SelectSingleNode('//*[local-name()="Counters"]')
        if ($null -eq $counters) { $problems.Add("trx has no Counters: $trxPath") }
        else {
            $counts.total = [int]$counters.total
            $counts.executed = [int]$counters.executed
            $counts.passed = [int]$counters.passed
            $counts.failed = [int]$counters.failed
            $counts.skipped = [int]$counters.notExecuted
            if ($counts.skipped -gt 0) { $problems.Add("$($counts.skipped) skipped test(s) - skipped is never green") }
            if ($counts.passed -le 0) { $problems.Add('passed == 0 - the filter matched no test case') }
            if ($counts.failed -gt 0) { $problems.Add("$($counts.failed) failed test(s)") }
            if ($counts.total -le 0) { $problems.Add('total == 0 - empty test run') }
        }
        $trxSha = (Get-FileHash -LiteralPath $trxPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$verdict = if ($problems.Count -eq 0) { 'PASS' } else { 'FAIL' }
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("qa_id: $Id")
$lines.Add("verdict: $verdict")
$lines.Add("command: $Command")
$lines.Add("exit_code: $exitCode")
$lines.Add("started_utc: $($startedUtc.ToString('o'))")
$lines.Add("finished_utc: $((Get-Date).ToUniversalTime().ToString('o'))")
$lines.Add("git_head: $head")
$lines.Add("git_status_porcelain: $(if ($status.Length -eq 0) { '(clean)' } else { $status })")
$lines.Add("trx: $trxPath")
$lines.Add("trx_sha256: $trxSha")
$lines.Add("tests_total: $($counts.total)")
$lines.Add("tests_executed: $($counts.executed)")
$lines.Add("tests_passed: $($counts.passed)")
$lines.Add("tests_failed: $($counts.failed)")
$lines.Add("tests_skipped: $($counts.skipped)")
$lines.Add("script: scripts\run-qa.ps1")
$lines.Add("script_sha256: $scriptSha")
$lines.Add("problems: $(if ($problems.Count -eq 0) { '(none)' } else { $problems -join ' | ' })")
[System.IO.File]::WriteAllText($evidencePath, (($lines -join "`n") + "`n"), $utf8)

# Evidence integrity: the written file must carry the bindings, and both hashes must match
# their recomputed values - otherwise this QA run counts as NOT EXECUTED.
$written = [System.IO.File]::ReadAllText($evidencePath)
if ($written.Length -eq 0) { $problems.Add('evidence file is empty') }
foreach ($key in @('command', 'exit_code', 'trx_sha256', 'git_head', 'script_sha256')) {
    if ($written -notmatch ("(?m)^" + [regex]::Escape($key + ': ') + '\S')) {
        $problems.Add("evidence file lacks a non-empty '$key' binding")
    }
}
if ($trxSha.Length -gt 0 -and $written -notmatch [regex]::Escape("trx_sha256: $trxSha")) {
    $problems.Add('evidence trx_sha256 does not match the recomputed artifact hash')
}
if ($written -notmatch [regex]::Escape("script_sha256: $scriptSha")) {
    $problems.Add('evidence script_sha256 does not match the recomputed script hash')
}
if ((Get-FileHash -LiteralPath $scriptPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $scriptSha) {
    $problems.Add('run-qa.ps1 changed while it was running')
}

if ($problems.Count -ne 0 -and $verdict -eq 'PASS') {
    $verdict = 'FAIL'
    $lines[1] = 'verdict: FAIL'
    $lines[$lines.Count - 1] = "problems: $($problems -join ' | ')"
    [System.IO.File]::WriteAllText($evidencePath, (($lines -join "`n") + "`n"), $utf8)
}

$evidenceSha = (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText($hashPath, "$trxSha  $trxPath`n$evidenceSha  $evidencePath`n", $utf8)

if ($verdict -eq 'PASS') {
    Write-Host ">> run-qa [$Id] PASS: passed=$($counts.passed) failed=$($counts.failed) skipped=$($counts.skipped) -> $evidencePath"
    exit 0
}
Write-Host ">> run-qa [$Id] FAIL: $($problems -join ' | ')"
Write-Host ">> evidence: $evidencePath"
exit 1

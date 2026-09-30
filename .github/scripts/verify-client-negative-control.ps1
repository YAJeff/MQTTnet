param([Parameter(Mandatory)][string]$CandidateRoot)
$ErrorActionPreference = 'Stop'
$artifact = Join-Path $CandidateRoot 'c13-baseline.zip'
$headers = @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json' }
Invoke-WebRequest -Headers $headers -Uri 'https://api.github.com/repos/YAJeff/MQTTnet/actions/artifacts/11127933702/zip' -OutFile $artifact
if ((Get-FileHash $artifact).Hash -ne '968E87E541CA96F8EDEF6F39D3B356E838CE36E503DDB6A5E4F9DD9D4C98BC0C') { throw 'Sealed c13 baseline archive mismatch' }
$baseline = Join-Path $CandidateRoot 'c13-baseline'
Expand-Archive -LiteralPath $artifact -DestinationPath $baseline
$manifest = Get-Content "$baseline/PACKAGE-MANIFEST.json" -Raw | ConvertFrom-Json
if ($manifest.commit -ne 'c13effe8d12edad407700f77e1c1802514f5b1de') { throw 'Wrong control source' }
foreach ($lib in $manifest.libraries) {
    foreach ($path in @("$baseline/references/$($lib.framework)/$($lib.package).dll", "$baseline/test-binaries/$($lib.framework)/$($lib.package).dll")) {
        if ((Get-FileHash $path).Hash -ne $lib.dllSha256) { throw "Control DLL mismatch: $path" }
    }
}
$control = Join-Path $CandidateRoot 'client-counterexample'
New-Item -ItemType Directory $control | Out-Null
Copy-Item "$CandidateRoot/test-binaries/net10.0/*" $control -Recurse
Copy-Item "$baseline/references/net10.0/*.dll" $control -Force
$harness = (Get-FileHash "$control/MQTTnet.Tests.dll").Hash
if ($harness -ne (Get-FileHash "$CandidateRoot/test-binaries/net10.0/MQTTnet.Tests.dll").Hash) { throw 'Counterexample test harness changed' }
dotnet "$control/MQTTnet.Tests.dll" --filter 'FullyQualifiedName~ClientNegativePubRec_Tests' --report-trx --report-trx-filename client-negative-control.trx --results-directory "$CandidateRoot/tests" *> "$CandidateRoot/tests/client-negative-control.log"
$exitCode = $LASTEXITCODE
[xml]$trx = Get-Content "$CandidateRoot/tests/client-negative-control.trx"
$counts = $trx.TestRun.ResultSummary.Counters
if ($exitCode -eq 0 -or $counts.total -ne '13' -or $counts.failed -ne '9' -or $counts.passed -ne '4' -or $counts.notExecuted -ne '0') {
    Get-Content "$CandidateRoot/tests/client-negative-control.log" -Tail 60
    throw "Counterexample did not prove expected nine defects and four unchanged positive cases: $($counts.OuterXml)"
}
@{ baselineSource = $manifest.commit; archiveSha256 = (Get-FileHash $artifact).Hash; harnessSha256 = $harness; controlDlls = $manifest.libraries; total = 13; expectedFailures = 9; positivePasses = 4; skipped = 0; exitCode = $exitCode; trxSha256 = (Get-FileHash "$CandidateRoot/tests/client-negative-control.trx").Hash } |
    ConvertTo-Json -Depth 7 | Set-Content "$CandidateRoot/CLIENT-COUNTEREXAMPLE.json"
# Keep compact receipts, not a second archived copy of the full baseline/harness.
# Only these owned, run-local subdirectories are removed; originals are retained in GitHub and locally.
$resolvedRoot = (Resolve-Path $CandidateRoot).Path
foreach ($owned in @($baseline, $control)) {
    $resolved = (Resolve-Path $owned).Path
    if (!$resolved.StartsWith($resolvedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)) { throw 'Cleanup target escaped owned qualification directory' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Remove-Item -LiteralPath $artifact
Write-Output 'Expected c13 client counterexample: nine failures, four positive passes, unchanged test DLL'
$global:LASTEXITCODE = 0

param([Parameter(Mandatory)][string]$SourceRoot, [Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
$SourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$Output = [IO.Path]::GetFullPath($Output)
$fixed = '14bfa28cbf0db07d8753203f767a21c7024408ec'
$baseline = '798a0e2250f40a241e0c8d2e7c9a366051be1b97'
if ((git -C $SourceRoot rev-parse HEAD) -ne $fixed) { throw 'Wrong public source' }
New-Item -ItemType Directory -Force "$Output/tests", "$Output/test-binaries" | Out-Null
Push-Location $SourceRoot
try {
    dotnet build Source/MQTTnet.Tests/MQTTnet.Tests.csproj -c Release -m:2 -p:BuildInParallel=false -p:AssemblyVersion=1.0.0.0 -p:Version=5.2.0-local.client.14bfa28c
    if ($LASTEXITCODE -ne 0) { throw 'Public build failed' }
    $identities = @()
    foreach ($tfm in @('net8.0', 'net10.0')) {
        $target = "$Output/test-binaries/$tfm"
        New-Item -ItemType Directory $target | Out-Null
        Copy-Item "Source/MQTTnet.Tests/bin/Release/$tfm/*" $target -Recurse
        dotnet "$target/MQTTnet.Tests.dll" --filter 'FullyQualifiedName~ClientNegativePubRec_Tests' --report-trx --report-trx-filename "public-linux-$tfm.trx" --results-directory "$Output/tests"
        if ($LASTEXITCODE -ne 0) { throw "Public focused suite failed: $tfm" }
        [xml]$trx = Get-Content "$Output/tests/public-linux-$tfm.trx"
        if ($trx.TestRun.ResultSummary.Counters.passed -ne '13' -or $trx.TestRun.ResultSummary.Counters.total -ne '13') { throw 'Incomplete public suite' }
        foreach ($library in @('MQTTnet', 'MQTTnet.Server', 'MQTTnet.AspNetCore', 'MQTTnet.Tests')) {
            $identities += @{ framework = $tfm; library = $library; sha256 = (Get-FileHash "$target/$library.dll").Hash }
        }
    }
    $harness = (Get-FileHash "$Output/test-binaries/net10.0/MQTTnet.Tests.dll").Hash
    # Compile only the original public client, then swap that DLL into an unchanged test harness.
    foreach ($file in @('Source/MQTTnet/MqttClient.cs', 'Source/MQTTnet/Publishing/MqttClientPublishResultFactory.cs')) {
        $original = git show "${baseline}:$file"
        [IO.File]::WriteAllText((Join-Path $SourceRoot $file), ($original -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
    }
    if (git diff $baseline -- Source/MQTTnet) { throw 'Baseline core client source is not exactly upstream master' }
    dotnet build Source/MQTTnet/MQTTnet.csproj -c Release -f net10.0 --no-restore -m:2 -p:BuildInParallel=false -p:GeneratePackageOnBuild=false -p:AssemblyVersion=1.0.0.0 -p:SourceRevisionId=$baseline -p:Version=5.2.0-local.client.base798a0e22
    if ($LASTEXITCODE -ne 0) { throw 'Public baseline client build failed' }
    $control = "$Output/public-master-control"
    New-Item -ItemType Directory $control | Out-Null
    Copy-Item "$Output/test-binaries/net10.0/*" $control -Recurse
    Copy-Item Source/MQTTnet/bin/Release/net10.0/MQTTnet.dll "$control/MQTTnet.dll" -Force
    if ((Get-FileHash "$control/MQTTnet.Tests.dll").Hash -ne $harness) { throw 'Public baseline test harness changed' }
    $baselineDll = (Get-FileHash "$control/MQTTnet.dll").Hash
    dotnet "$control/MQTTnet.Tests.dll" --filter 'FullyQualifiedName~ClientNegativePubRec_Tests' --report-trx --report-trx-filename public-master-control.trx --results-directory "$Output/tests" *> "$Output/tests/public-master-control.log"
    $exitCode = $LASTEXITCODE
    [xml]$controlTrx = Get-Content "$Output/tests/public-master-control.trx"
    $counts = $controlTrx.TestRun.ResultSummary.Counters
    if ($exitCode -eq 0 -or $counts.total -ne '13' -or $counts.failed -ne '9' -or $counts.passed -ne '4' -or $counts.notExecuted -ne '0') {
        Get-Content "$Output/tests/public-master-control.log" -Tail 60
        throw "Public baseline counterexample mismatch: $($counts.OuterXml)"
    }
    @{ fixedSource = $fixed; baselineSource = $baseline; baselineClientSha256 = $baselineDll; unchangedHarnessSha256 = $harness; baselineFailures = 9; baselinePositivePasses = 4; fixedPassesPerFramework = 13; identities = $identities } |
        ConvertTo-Json -Depth 7 | Set-Content "$Output/PUBLIC-CLIENT-RECEIPT.json"
    # Preserve baseline DLL and proof, while run-local full control binaries expire with the runner.
    Copy-Item "$control/MQTTnet.dll" "$Output/baseline-MQTTnet-net10.dll"
    $resolvedRoot = (Resolve-Path $Output).Path
    $resolvedControl = (Resolve-Path $control).Path
    if (!$resolvedControl.StartsWith($resolvedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)) { throw 'Control cleanup escaped owned output' }
    Remove-Item -LiteralPath $resolvedControl -Recurse -Force
    dotnet --info | Set-Content "$Output/linux-dotnet-info.txt"
} finally { Pop-Location }

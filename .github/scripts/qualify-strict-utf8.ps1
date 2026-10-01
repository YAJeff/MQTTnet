$ErrorActionPreference='Stop'
$root='utf8-qualification'
$baseline='abfa18f51481d8e988689bdb9e0b597a1e45372b'
$version="5.2.0-local.utf8.$($env:GITHUB_SHA.Substring(0,8))"
New-Item -ItemType Directory -Force "$root/tests" | Out-Null
dotnet build Source/MQTTnet.Tests/MQTTnet.Tests.csproj -c Release -m:2 -p:BuildInParallel=false "-p:Version=$version" -p:AssemblyVersion=1.0.0.0
if($LASTEXITCODE -ne 0){throw 'Build failed'}
$dlls=foreach($tfm in @('net8.0','net10.0')){
    New-Item -ItemType Directory -Force "$root/test-binaries/$tfm" | Out-Null
    Copy-Item "Source/MQTTnet.Tests/bin/Release/$tfm/*" "$root/test-binaries/$tfm" -Recurse
    foreach($id in @('MQTTnet','MQTTnet.Server','MQTTnet.AspNetCore','MQTTnet.Tests')){
        $path="$root/test-binaries/$tfm/$id.dll"
        $identity=[Reflection.AssemblyName]::GetAssemblyName($path).Version.ToString()
        if($id -ne 'MQTTnet.Tests' -and $identity -ne '1.0.0.0'){throw 'ABI changed'}
        @{framework=$tfm;library=$id;sha256=(Get-FileHash $path).Hash;assemblyVersion=$identity}
    }
}
@{source=$env:GITHUB_SHA;baseline=$baseline;version=$version;dlls=$dlls;packagePublished=$false;cost=@{standardPublicRunners=$true;expectedIncrementalComputeUsd=0;artifactRetentionDays=7;actualBillingUnavailable=$true;noObservedSavingsClaim=$true}} | ConvertTo-Json -Depth 6 | Set-Content "$root/DLL-MANIFEST.json"
foreach($tfm in @('net8.0','net10.0')){
    dotnet "$root/test-binaries/$tfm/MQTTnet.Tests.dll" --filter 'FullyQualifiedName~StrictUtf8' --report-trx --report-trx-filename "focused-linux-$tfm.trx" --results-directory "$root/tests"
    if($LASTEXITCODE -ne 0){throw 'Focused Linux failure'}
}
$files=@('Source/MQTTnet/Formatter/MqttBufferReader.cs','Source/MQTTnet/Formatter/V5/MqttV5PropertiesReader.cs')
git restore "--source=$baseline" -- @files
if($LASTEXITCODE -ne 0){throw 'Baseline restore failed'}
git diff --exit-code $baseline -- Source/MQTTnet
if($LASTEXITCODE -ne 0){throw 'Baseline client source differs'}
dotnet build Source/MQTTnet/MQTTnet.csproj -c Release -f net10.0 -m:2 -p:BuildInParallel=false -p:Version=5.2.0-local.incoming.abfa18f5 -p:AssemblyVersion=1.0.0.0 "-p:SourceRevisionId=$baseline"
if($LASTEXITCODE -ne 0){throw 'Baseline client build failed'}
Copy-Item "$root/test-binaries/net10.0/MQTTnet.dll" "$root/fixed-MQTTnet-net10.dll"
Copy-Item Source/MQTTnet/bin/Release/net10.0/MQTTnet.dll "$root/baseline-MQTTnet-net10.dll"
Copy-Item "$root/baseline-MQTTnet-net10.dll" "$root/test-binaries/net10.0/MQTTnet.dll" -Force
$harnessHash=(Get-FileHash "$root/test-binaries/net10.0/MQTTnet.Tests.dll").Hash
dotnet "$root/test-binaries/net10.0/MQTTnet.Tests.dll" --filter 'FullyQualifiedName~StrictUtf8' --report-trx --report-trx-filename baseline-counterexample.trx --results-directory "$root/tests"
$controlExit=$LASTEXITCODE
[xml]$trx=Get-Content "$root/tests/baseline-counterexample.trx"
$c=$trx.TestRun.ResultSummary.Counters
if($controlExit -eq 0 -or $c.total -ne '26' -or $c.failed -ne '18' -or $c.passed -ne '8'){throw 'Wrong baseline counterexample'}
if((Get-FileHash "$root/test-binaries/net10.0/MQTTnet.Tests.dll").Hash -ne $harnessHash){throw 'Harness changed'}
@{baseline=$baseline;baselineDllSha256=(Get-FileHash "$root/baseline-MQTTnet-net10.dll").Hash;harnessSha256=$harnessHash;total=26;expectedFailures=18;positivePasses=8;exit=$controlExit} | ConvertTo-Json | Set-Content "$root/COUNTEREXAMPLE.json"
Copy-Item "$root/fixed-MQTTnet-net10.dll" "$root/test-binaries/net10.0/MQTTnet.dll" -Force
git restore "--source=$env:GITHUB_SHA" -- @files
if($LASTEXITCODE -ne 0){throw 'Fixed source restore failed'}
$global:LASTEXITCODE=0
dotnet "$root/test-binaries/net10.0/MQTTnet.Tests.dll" --report-trx --report-trx-filename full-linux-net10.trx --results-directory "$root/tests"
if($LASTEXITCODE -ne 0){throw 'Full Linux failure'}
dotnet --info | Set-Content "$root/linux-dotnet-info.txt"

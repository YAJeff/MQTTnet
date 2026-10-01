$ErrorActionPreference='Stop'
Push-Location public-client
try{
    $source='184fdb4fb9f74dd41200a79a8f8f7ba9999c9084'
    $baseline='798a0e2250f40a241e0c8d2e7c9a366051be1b97'
    $root='utf8-public'
    New-Item -ItemType Directory -Force "$root/tests" | Out-Null
    dotnet build Source/MQTTnet.Tests/MQTTnet.Tests.csproj -c Release -m:2 -p:BuildInParallel=false -p:AssemblyVersion=1.0.0.0 "-p:SourceRevisionId=$source"
    if($LASTEXITCODE -ne 0){throw 'Public build failed'}
    $dlls=foreach($tfm in @('net8.0','net10.0')){
        New-Item -ItemType Directory -Force "$root/test-binaries/$tfm" | Out-Null
        Copy-Item "Source/MQTTnet.Tests/bin/Release/$tfm/*" "$root/test-binaries/$tfm" -Recurse
        foreach($id in @('MQTTnet','MQTTnet.Server','MQTTnet.AspNetCore','MQTTnet.Tests')){@{framework=$tfm;library=$id;sha256=(Get-FileHash "$root/test-binaries/$tfm/$id.dll").Hash}}
        dotnet "$root/test-binaries/$tfm/MQTTnet.Tests.dll" --filter 'FullyQualifiedName~StrictUtf8' --report-trx --report-trx-filename "focused-linux-$tfm.trx" --results-directory "$root/tests" | Out-Host
        if($LASTEXITCODE -ne 0){throw 'Public focused failure'}
    }
    @{source=$source;baseline=$baseline;dlls=$dlls} | ConvertTo-Json -Depth 5 | Set-Content "$root/MANIFEST.json"
    $files=@('Source/MQTTnet/Formatter/MqttBufferReader.cs','Source/MQTTnet/Formatter/V5/MqttV5PropertiesReader.cs')
    git restore "--source=$baseline" -- @files
    git diff --exit-code $baseline -- Source/MQTTnet
    if($LASTEXITCODE -ne 0){throw 'Master client not byte-exact'}
    dotnet build Source/MQTTnet/MQTTnet.csproj -c Release -f net10.0 -m:2 -p:AssemblyVersion=1.0.0.0 "-p:SourceRevisionId=$baseline"
    if($LASTEXITCODE -ne 0){throw 'Master build failure'}
    Copy-Item "$root/test-binaries/net10.0/MQTTnet.dll" "$root/fixed-MQTTnet-net10.dll"
    Copy-Item Source/MQTTnet/bin/Release/net10.0/MQTTnet.dll "$root/baseline-MQTTnet-net10.dll"
    Copy-Item "$root/baseline-MQTTnet-net10.dll" "$root/test-binaries/net10.0/MQTTnet.dll" -Force
    $h=(Get-FileHash "$root/test-binaries/net10.0/MQTTnet.Tests.dll").Hash
    dotnet "$root/test-binaries/net10.0/MQTTnet.Tests.dll" --filter 'FullyQualifiedName~StrictUtf8' --report-trx --report-trx-filename master-counterexample.trx --results-directory "$root/tests"
    $exit=$LASTEXITCODE
    [xml]$t=Get-Content "$root/tests/master-counterexample.trx";$c=$t.TestRun.ResultSummary.Counters
    if($exit -eq 0 -or $c.total -ne '26' -or $c.failed -ne '18' -or $c.passed -ne '8'){throw 'Wrong master counterexample'}
    if((Get-FileHash "$root/test-binaries/net10.0/MQTTnet.Tests.dll").Hash -ne $h){throw 'Harness changed'}
    @{source=$source;baseline=$baseline;expectedFailures=18;positivePasses=8;harnessSha256=$h;baselineDllSha256=(Get-FileHash "$root/baseline-MQTTnet-net10.dll").Hash} | ConvertTo-Json | Set-Content "$root/COUNTEREXAMPLE.json"
    Copy-Item "$root/fixed-MQTTnet-net10.dll" "$root/test-binaries/net10.0/MQTTnet.dll" -Force
    git restore "--source=$source" -- @files
    $global:LASTEXITCODE=0
    dotnet "$root/test-binaries/net10.0/MQTTnet.Tests.dll" --report-trx --report-trx-filename full-linux-net10.trx --results-directory "$root/tests"
    if($LASTEXITCODE -ne 0){throw 'Public full Linux failure'}
}finally{Pop-Location}

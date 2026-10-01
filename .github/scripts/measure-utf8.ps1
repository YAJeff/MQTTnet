$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force utf8-performance | Out-Null
$fixed=Get-Content .github/qualified/DLL-MANIFEST.json -Raw | ConvertFrom-Json
$before=Get-Content baseline/PACKAGE-MANIFEST.json -Raw | ConvertFrom-Json
if($fixed.source -ne '47b6eacea398b9097f4282e4a8de962f138eda0a' -or $before.commit -ne 'abfa18f51481d8e988689bdb9e0b597a1e45372b'){throw 'Wrong sealed source'}
$identities=foreach($tfm in @('net8.0','net10.0')){
    $f=@($fixed.dlls | Where-Object {$_.library -eq 'MQTTnet' -and $_.framework -eq $tfm})[0]
    $b=@($before.libraries | Where-Object {$_.package -eq 'MQTTnet' -and $_.framework -eq $tfm})[0]
    if((Get-FileHash ".github/qualified/test-binaries/$tfm/MQTTnet.dll").Hash -ne $f.sha256 -or (Get-FileHash "baseline/test-binaries/$tfm/MQTTnet.dll").Hash -ne $b.dllSha256){throw 'DLL mismatch'}
    @{framework=$tfm;baselineDllSha256=$b.dllSha256;fixedDllSha256=$f.sha256}
}
dotnet build .github/utf8-bench/Utf8Bench.csproj -c Release -m:2 -p:BuildInParallel=false
if($LASTEXITCODE -ne 0){throw 'Benchmark build failed'}
$samples=@()
foreach($tfm in @('net8.0','net10.0')){
    $bin=".github/utf8-bench/bin/Release/$tfm"
    $benchHash=(Get-FileHash "$bin/Utf8Bench.dll").Hash
    for($round=0;$round -lt 7;$round++){
        $order=if($round % 2 -eq 0){@('baseline','fixed')}else{@('fixed','baseline')}
        foreach($variant in $order){
            $library=if($variant -eq 'fixed'){".github/qualified/test-binaries/$tfm/MQTTnet.dll"}else{"baseline/test-binaries/$tfm/MQTTnet.dll"}
            Copy-Item $library "$bin/MQTTnet.dll" -Force
            if((Get-FileHash "$bin/Utf8Bench.dll").Hash -ne $benchHash){throw 'Benchmark changed'}
            $raw=dotnet "$bin/Utf8Bench.dll"
            if($LASTEXITCODE -ne 0){throw 'Benchmark execution failed'}
            $path="utf8-performance/$tfm-$variant-$round.json"
            $raw | Set-Content $path
            $samples+=@{framework=$tfm;variant=$variant;round=$round;benchmarkSha256=$benchHash;receiptSha256=(Get-FileHash $path).Hash;sample=($raw | ConvertFrom-Json)}
        }
    }
}
function Median($values){$sorted=@($values | Sort-Object);return $sorted[[int]($sorted.Count/2)]}
$comparisons=foreach($tfm in @('net8.0','net10.0')){
    $names=$samples | Where-Object framework -eq $tfm | Select-Object -First 1 | ForEach-Object {$_.sample.results.name}
    foreach($name in $names){
        $b=@($samples | Where-Object {$_.framework -eq $tfm -and $_.variant -eq 'baseline'} | ForEach-Object {$_.sample.results | Where-Object name -eq $name})
        $f=@($samples | Where-Object {$_.framework -eq $tfm -and $_.variant -eq 'fixed'} | ForEach-Object {$_.sample.results | Where-Object name -eq $name})
        $bt=Median $b.operationsPerSecond;$ft=Median $f.operationsPerSecond
        @{framework=$tfm;case=$name;baselineMedianOpsPerSecond=$bt;fixedMedianOpsPerSecond=$ft;throughputDeltaPercent=100*($ft/$bt-1);baselineMedianAllocatedBytesPerOp=(Median $b.allocatedBytesPerOperation);fixedMedianAllocatedBytesPerOp=(Median $f.allocatedBytesPerOperation)}
    }
}
@{baseline=$before.commit;fixed=$fixed.source;identities=$identities;rounds=7;order='Alternating baseline/fixed order, separate process each sample, 2000 warmup calls per case';comparisons=$comparisons;samples=$samples;scope='In-process valid string/property/MQTT packet decoding only; no wire/broker/CoreMQ capacity or universal performance-neutral claim';sharedRunnerVariance=$true} | ConvertTo-Json -Depth 10 | Set-Content utf8-performance/COMPARISON.json
dotnet --info | Set-Content utf8-performance/dotnet-info.txt

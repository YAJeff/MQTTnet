$ErrorActionPreference='Stop'
$output=(New-Item -ItemType Directory -Force utf8-triple-performance).FullName
$sources=@{
    baseline=@{source='798a0e2250f40a241e0c8d2e7c9a366051be1b97';directory='optimized/baseline';manifest='DLL-MANIFEST.json'}
    qualified=@{source='184fdb4fb9f74dd41200a79a8f8f7ba9999c9084';directory='.github/qualified';manifest='MANIFEST.json'}
    optimized=@{source='cc1373edcea2a7adf1a0709f891706f979833ce7';directory='optimized';manifest='DLL-MANIFEST.json'}
}
$identities=foreach($variant in @('baseline','qualified','optimized')){
    $v=$sources[$variant];$m=Get-Content "$($v.directory)/$($v.manifest)" -Raw | ConvertFrom-Json
    $actualSource=$m.source
    if($actualSource -ne $v.source){throw 'Wrong sealed source'}
    foreach($tfm in @('net8.0','net10.0')){
        $entry=@($m.dlls | Where-Object {$_.library -eq 'MQTTnet' -and $_.framework -eq $tfm})[0]
        $hash=$entry.sha256
        if((Get-FileHash "$($v.directory)/test-binaries/$tfm/MQTTnet.dll").Hash -ne $hash){throw 'DLL identity mismatch'}
        @{variant=$variant;framework=$tfm;source=$actualSource;sha256=$hash}
    }
}
dotnet build .github/utf8-bench/Utf8Bench.csproj -c Release -m:2 -p:BuildInParallel=false
if($LASTEXITCODE -ne 0){throw 'Benchmark build failed'}
$samples=@()
foreach($tfm in @('net8.0','net10.0')){
    $bin=".github/utf8-bench/bin/Release/$tfm";$benchHash=(Get-FileHash "$bin/Utf8Bench.dll").Hash
    for($round=0;$round -lt 7;$round++){
        $order=switch($round % 3){0{@('baseline','qualified','optimized')}1{@('qualified','optimized','baseline')}2{@('optimized','baseline','qualified')}}
        foreach($variant in $order){
            Copy-Item "$($sources[$variant].directory)/test-binaries/$tfm/MQTTnet.dll" "$bin/MQTTnet.dll" -Force
            if((Get-FileHash "$bin/Utf8Bench.dll").Hash -ne $benchHash){throw 'Harness changed'}
            $raw=dotnet "$bin/Utf8Bench.dll";if($LASTEXITCODE -ne 0){throw 'Benchmark failed'}
            $path="$output/$tfm-$variant-$round.json";$raw | Set-Content $path
            $samples+=@{framework=$tfm;variant=$variant;round=$round;benchmarkSha256=$benchHash;receiptSha256=(Get-FileHash $path).Hash;sample=($raw | ConvertFrom-Json)}
        }
    }
}
function Median($values){$s=@($values | Sort-Object);return $s[[int]($s.Count/2)]}
$comparisons=foreach($tfm in @('net8.0','net10.0')){
    $names=@($samples | Where-Object framework -eq $tfm)[0].sample.results.name
    foreach($name in $names){
        $medians=@{}
        foreach($variant in @('baseline','qualified','optimized')){
            $cases=@($samples | Where-Object {$_.framework -eq $tfm -and $_.variant -eq $variant} | ForEach-Object {$_.sample.results | Where-Object name -eq $name})
            $medians[$variant]=@{operationsPerSecond=(Median $cases.operationsPerSecond);allocatedBytesPerOperation=(Median $cases.allocatedBytesPerOperation)}
        }
        @{framework=$tfm;case=$name;medians=$medians;optimizedVsQualifiedThroughputPercent=100*($medians.optimized.operationsPerSecond/$medians.qualified.operationsPerSecond-1);optimizedVsBaselineThroughputPercent=100*($medians.optimized.operationsPerSecond/$medians.baseline.operationsPerSecond-1)}
    }
}
@{source='cc1373edcea2a7adf1a0709f891706f979833ce7';identities=$identities;rounds=7;order='Rotating three-variant order, separate process each sample, minimum200ms warmup and300ms measurement per case';comparisons=$comparisons;samples=$samples;scope='In-process valid decoding only; shared runner/JIT variance; payload case still decodes short topic; no broker/CoreMQ capacity or universal performance-neutral claim'} | ConvertTo-Json -Depth 12 | Set-Content "$output/COMPARISON.json"
dotnet --info | Set-Content "$output/dotnet-info.txt"



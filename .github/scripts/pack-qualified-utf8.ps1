$ErrorActionPreference='Stop'
$source='47b6eacea398b9097f4282e4a8de962f138eda0a'
$version='5.2.0-local.utf8.47b6eace'
$artifactDigest='3EBE76760D52C940DA386EDE2756D2CF8B812B0D48E308A0F4A61A83F20CBFC2'
$root=(New-Item -ItemType Directory -Force utf8-packages).FullName
$qualified=(New-Item -ItemType Directory -Force qualified-artifact).FullName
curl --fail --silent --show-error --location --header "Authorization: Bearer $env:GITHUB_TOKEN" --header 'Accept: application/vnd.github+json' 'https://api.github.com/repos/YAJeff/MQTTnet/actions/artifacts/11146455508/zip' --output qualified-artifact.zip
if($LASTEXITCODE -ne 0){throw 'Qualified artifact download failed'}
if((Get-FileHash qualified-artifact.zip).Hash -ne $artifactDigest){throw 'Qualified ZIP digest mismatch'}
Expand-Archive -LiteralPath qualified-artifact.zip -DestinationPath $qualified
$manifest=Get-Content "$qualified/DLL-MANIFEST.json" -Raw | ConvertFrom-Json
if($manifest.source -ne $source -or $manifest.version -ne $version -or @($manifest.dlls).Count -ne 8){throw 'Wrong qualification identity'}
foreach($d in $manifest.dlls){
    $p="$qualified/test-binaries/$($d.framework)/$($d.library).dll"
    if((Get-FileHash $p).Hash -ne $d.sha256 -or [Reflection.AssemblyName]::GetAssemblyName($p).Version.ToString() -ne '1.0.0.0'){throw 'Qualified DLL identity mismatch'}
}
Copy-Item "$qualified/DLL-MANIFEST.json" "$root/QUALIFIED-DLL-MANIFEST.json"
Copy-Item "$qualified/COUNTEREXAMPLE.json" "$root/COUNTEREXAMPLE.json"
Copy-Item "$qualified/tests" "$root/qualification-tests" -Recurse
$guard=Join-Path $root 'NoCompile.targets'
'<Project><Target Name="RejectUnexpectedCompilation" BeforeTargets="CoreCompile"><Error Text="Artifact-only packaging must not compile source." /></Target></Project>' | Set-Content $guard
$projects=@('MQTTnet','MQTTnet.Server','MQTTnet.AspNetCore')
$bindings=@()
Push-Location qualified-source
try{
    if((git rev-parse HEAD) -ne $source){throw 'Wrong checkout'}
    dotnet restore Source/MQTTnet.AspNetCore/MQTTnet.AspNetCore.csproj "-p:Version=$version" "-p:PackageVersion=$version" -p:AssemblyVersion=1.0.0.0 "-p:SourceRevisionId=$source" "-p:CustomAfterMicrosoftCommonTargets=$guard"
    if($LASTEXITCODE -ne 0){throw 'Restore failed'}
    foreach($id in $projects){
        foreach($tfm in @('net8.0','net10.0')){
            $bin=(New-Item -ItemType Directory -Force "Source/$id/bin/Release/$tfm").FullName
            foreach($ext in @('dll','xml','pdb')){
                $inputFile="$qualified/test-binaries/$tfm/$id.$ext"
                if(!(Test-Path -LiteralPath $inputFile)){throw "Missing qualified $id.$ext"}
                Copy-Item $inputFile "$bin/$id.$ext"
                $bindings+=@{library=$id;framework=$tfm;file="$id.$ext";sha256=(Get-FileHash $inputFile).Hash}
            }
        }
    }
    foreach($id in $projects){
        dotnet pack "Source/$id/$id.csproj" -c Release --no-build --no-restore --output "$root/packages" "-p:Version=$version" "-p:PackageVersion=$version" -p:AssemblyVersion=1.0.0.0 "-p:SourceRevisionId=$source" "-p:CustomAfterMicrosoftCommonTargets=$guard"
        if($LASTEXITCODE -ne 0){throw "Pack failed for $id"}
    }
    git diff --exit-code
    if($LASTEXITCODE -ne 0){throw 'Qualified source changed'}
}finally{Pop-Location}
$packages=@(Get-ChildItem "$root/packages/*.nupkg")
if($packages.Count -ne 3){throw 'Wrong package count'}
$receipts=foreach($package in $packages){
    $zip=[IO.Compression.ZipFile]::OpenRead($package.FullName)
    try{
        $nuspec=$zip.Entries | Where-Object {$_.FullName.EndsWith('.nuspec')}
        $reader=[IO.StreamReader]::new($nuspec.Open())
        try{[xml]$spec=$reader.ReadToEnd()}finally{$reader.Dispose()}
        $id=$spec.package.metadata.id
        if($id -notin $projects -or $spec.package.metadata.version -ne $version -or $spec.package.metadata.repository.commit -ne $source){throw 'Nuspec source/version mismatch'}
        $deps=@($spec.SelectNodes("//*[local-name()='dependency']") | ForEach-Object {@{id=$_.id;version=$_.version}})
        foreach($dep in $deps){if($dep.id -in $projects -and $dep.version -notin @($version,"[$version]")){throw 'Split native dependency'}}
        if($id -eq 'MQTTnet.Server' -and @($deps | Where-Object id -eq 'MQTTnet').Count -ne 2){throw 'Missing server dependency frameworks'}
        if($id -eq 'MQTTnet.AspNetCore' -and @($deps | Where-Object {$_.id -in @('MQTTnet','MQTTnet.Server')}).Count -ne 4){throw 'Missing ASP dependencies'}
        $dllEntries=@($zip.Entries | Where-Object {$_.FullName -like 'lib/*/*.dll'})
        if($dllEntries.Count -ne 2){throw 'Wrong packaged DLL count'}
        $dlls=foreach($tfm in @('net8.0','net10.0')){
            $entry=$zip.GetEntry("lib/$tfm/$id.dll")
            if(!$entry){throw 'Missing packaged DLL'}
            $stream=$entry.Open();try{$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
            $expected=@($manifest.dlls | Where-Object {$_.library -eq $id -and $_.framework -eq $tfm})[0]
            if($hash -ne $expected.sha256){throw 'Packaged DLL differs from tested DLL'}
            @{framework=$tfm;library=$id;sha256=$hash;assemblyVersion=$expected.assemblyVersion}
        }
        @{package=$id;file=$package.Name;sha256=(Get-FileHash $package.FullName).Hash;dlls=$dlls;dependencies=$deps}
    }finally{$zip.Dispose()}
}
$symbols=@(Get-ChildItem "$root/packages/*.snupkg")
if($symbols.Count -ne 3){throw 'Missing qualified symbol packages'}
foreach($symbol in $symbols){
    $id=$symbol.Name.Substring(0,$symbol.Name.Length-(".$version.snupkg").Length)
    $zip=[IO.Compression.ZipFile]::OpenRead($symbol.FullName)
    try{foreach($tfm in @('net8.0','net10.0')){
        $entry=$zip.GetEntry("lib/$tfm/$id.pdb");if(!$entry){throw 'Missing packaged PDB'}
        $stream=$entry.Open();try{$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
        $expected=@($bindings | Where-Object {$_.library -eq $id -and $_.framework -eq $tfm -and $_.file -eq "$id.pdb"})[0]
        if($hash -ne $expected.sha256){throw 'Packaged PDB differs'}
    }}finally{$zip.Dispose()}
}
@{source=$source;version=$version;wrapper=$env:GITHUB_SHA;qualifiedRun=36827676935;qualifiedArtifact=11146455508;qualifiedZipSha256=$artifactDigest;qualifiedManifestSha256=(Get-FileHash "$root/QUALIFIED-DLL-MANIFEST.json").Hash;packages=$receipts;symbols=@($symbols | ForEach-Object {@{file=$_.Name;sha256=(Get-FileHash $_.FullName).Hash}});qualifiedSidecars=$bindings;compiled=$false;feedPublished=$false;coreMqChanged=$false;scope='Artifact-only packaging of six already qualified native DLLs; not CoreMQ adoption or runtime capacity';cost=@{publicStandardRunner=$true;expectedIncrementalComputeUsd=0;actualBillingUnavailable=$true;retentionDays=7;ongoingResources=0}} | ConvertTo-Json -Depth 10 | Set-Content "$root/PACKAGE-MANIFEST.json"
dotnet --info | Set-Content "$root/dotnet-info.txt"

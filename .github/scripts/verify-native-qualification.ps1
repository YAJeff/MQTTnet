param([string]$PackageRoot, [string]$TestRoot, [string]$Output, [string]$Version, [string]$Commit)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$records = @()
foreach ($id in @('MQTTnet','MQTTnet.Server','MQTTnet.AspNetCore')) {
    $package = Join-Path $PackageRoot "$id.$Version.nupkg"
    $archive = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $nuspec = $archive.Entries | Where-Object FullName -like '*.nuspec' | Select-Object -First 1
        $reader = [IO.StreamReader]::new($nuspec.Open())
        try { [xml]$metadata = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($metadata.package.metadata.version -ne $Version -or $metadata.package.metadata.repository.commit -ne $Commit) { throw "Package source/version mismatch: $id" }
        foreach ($tfm in @('net8.0','net10.0')) {
            $entry = $archive.GetEntry("lib/$tfm/$id.dll")
            if (!$entry) { throw "Missing packaged DLL: $id/$tfm" }
            $folder = Join-Path $Output "references/$tfm"
            New-Item -ItemType Directory -Force $folder | Out-Null
            $dll = Join-Path $folder "$id.dll"
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dll, $true)
            $hash = (Get-FileHash $dll -Algorithm SHA256).Hash
            $testDll = Join-Path $TestRoot "$tfm/$id.dll"
            if ((Get-FileHash $testDll -Algorithm SHA256).Hash -ne $hash) { throw "Packaged/test DLL mismatch: $id/$tfm" }
            $identity = [Reflection.AssemblyName]::GetAssemblyName($dll)
            if ($identity.Version.ToString() -ne '1.0.0.0') { throw "Binary compatibility identity changed: $id/$tfm" }
            $records += [pscustomobject]@{package=$id;framework=$tfm;dllSha256=$hash;assemblyVersion=$identity.Version.ToString();testDllExact=$true;packageSha256=(Get-FileHash $package -Algorithm SHA256).Hash}
        }
    } finally { $archive.Dispose() }
}
New-Item -ItemType Directory -Force $Output | Out-Null
@{commit=$Commit;version=$Version;libraries=$records;cost=@{repository='YAJeff/MQTTnet';visibility='public';runners='standard ubuntu-latest and windows-latest';expectedIncrementalComputeUsd=0;computeBasis='https://docs.github.com/en/billing/concepts/product-billing/github-actions';artifactRetentionDays=7;intendedLifetime='Single qualification run; download evidence locally before expiry';billing='Actual account billing unavailable to public API; no observed billed savings claimed';owner='CoreVar native MQTTnet qualification'}} | ConvertTo-Json -Depth 7 | Set-Content (Join-Path $Output 'PACKAGE-MANIFEST.json')
$records | Format-Table -AutoSize

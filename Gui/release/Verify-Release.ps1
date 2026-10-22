param(
    [Parameter(Mandatory=$true)][string]$PackageDirectory,
    [string]$SourceDirectory=(Split-Path $PSScriptRoot -Parent),
    [string]$RebuildRoot
)
$ErrorActionPreference='Stop'
$PackageDirectory=[IO.Path]::GetFullPath($PackageDirectory)
$SourceDirectory=[IO.Path]::GetFullPath($SourceDirectory)
$releaseRoot=Split-Path $PackageDirectory -Parent
$name=Split-Path $PackageDirectory -Leaf
$zipPath=$PackageDirectory+'.zip'
$manifest=Get-Content -LiteralPath (Join-Path $PackageDirectory 'release-manifest.json') -Raw | ConvertFrom-Json
if($manifest.Version -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid release version.'}
$expectedVersion=$manifest.Version+'.0'
$checked=0
foreach($record in $manifest.Files){
    $path=Join-Path $PackageDirectory $record.Path
    if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $record.Sha256 -or (Get-Item -LiteralPath $path).Length -ne $record.Bytes){throw ('Package manifest mismatch: '+$record.Path)}
    $checked++
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Get-StreamHash($Stream){$sha=[Security.Cryptography.SHA256]::Create();try{([BitConverter]::ToString($sha.ComputeHash($Stream))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}}
$archive=[IO.Compression.ZipFile]::OpenRead($zipPath)
$zipChecked=0
try{
    foreach($entry in $archive.Entries | Where-Object {$_.Name}){
        $relative=($entry.FullName -split '/',2)[1]
        if(!$relative -or [IO.Path]::IsPathRooted($relative) -or $relative -match '(^|/)\.\.(/|$)'){throw 'Unexpected outer zip path.'}
        $stream=$entry.Open()
        try{$entryHash=Get-StreamHash $stream}finally{$stream.Dispose()}
        if($entryHash -ine (Get-FileHash -LiteralPath (Join-Path $PackageDirectory $relative) -Algorithm SHA256).Hash){throw ('ZIP content differs: '+$relative)}
        $zipChecked++
    }
}finally{$archive.Dispose()}
$sourceZip=Join-Path $PackageDirectory $manifest.SourceArchive
$sourceArchive=[IO.Compression.ZipFile]::OpenRead($sourceZip)
$sourceChecked=0
$engineSourceChecked=0
$engineProvenance=Get-Content -LiteralPath (Join-Path $PackageDirectory 'engine/provenance.json') -Raw | ConvertFrom-Json
$patchDateProvenance=Get-Content -LiteralPath (Join-Path $PackageDirectory 'data/patch-dates/provenance.json') -Raw | ConvertFrom-Json
$engineSourceDirectory=Join-Path (Split-Path $SourceDirectory -Parent) 'depotdownloader-historical-engine'
try{
    $expected=@(Get-ChildItem -LiteralPath $SourceDirectory -File | Where-Object {$_.Extension -in @('.cs','.ps1')})
    foreach($folder in @('core','release','tests')){
        $expected+=@(Get-ChildItem -LiteralPath (Join-Path $SourceDirectory $folder) -File -Recurse | Where-Object {$_.Extension -in @('.cs','.ps1') -or ($folder -eq 'release' -and $_.Extension -in @('.txt','.nuspec')) -or ($folder -eq 'core' -and $_.Name -in @('App.config','app.manifest','LICENSE'))})
    }
    foreach($file in $expected){
        $relative=$file.FullName.Substring($SourceDirectory.Length+1).Replace('\','/')
        $entry=$sourceArchive.GetEntry('Gui/'+$relative)
        if(!$entry){throw ('Source missing: '+$relative)}
        $stream=$entry.Open()
        try{$entryHash=Get-StreamHash $stream}finally{$stream.Dispose()}
        if($entryHash -ine (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash){throw ('Frozen source differs: '+$relative)}
        $sourceChecked++
    }
    foreach($record in $engineProvenance.SourceFiles){
        $relative='DepotDownloader/'+$record.Path
        $entry=$sourceArchive.GetEntry('DepotDownloader/'+$relative)
        if(!$entry){throw ('Modified engine source missing: '+$relative)}
        $stream=$entry.Open()
        try{$entryHash=Get-StreamHash $stream}finally{$stream.Dispose()}
        if($entryHash -ine $record.Sha256 -or $entryHash -ine (Get-FileHash -LiteralPath (Join-Path $engineSourceDirectory $relative) -Algorithm SHA256).Hash){throw ('Modified engine source differs: '+$relative)}
        $engineSourceChecked++
    }
    foreach($relative in @('HISTORICAL-COMPATIBILITY.txt','Build-HistoricalEngine.ps1','Tests/HistoricalDepotPolicyTests.cs','Tests/HistoricalDepotPolicyTests.csproj')){
        $entry=$sourceArchive.GetEntry('DepotDownloader/'+$relative)
        if(!$entry){throw ('Modified engine support file missing: '+$relative)}
        $stream=$entry.Open()
        try{$entryHash=Get-StreamHash $stream}finally{$stream.Dispose()}
        if($entryHash -ine (Get-FileHash -LiteralPath (Join-Path $engineSourceDirectory $relative) -Algorithm SHA256).Hash){throw ('Modified engine support file differs: '+$relative)}
        $engineSourceChecked++
    }
    $entry=$sourceArchive.GetEntry('PatchDateCatalog/Build-Catalog.ps1')
    if(!$entry){throw 'Patch date catalog generator source missing.'}
    $stream=$entry.Open()
    try{$generatorHash=Get-StreamHash $stream}finally{$stream.Dispose()}
    if($generatorHash -ine $patchDateProvenance.GeneratorSha256 -or $generatorHash -ine (Get-FileHash -LiteralPath (Join-Path (Split-Path $SourceDirectory -Parent) 'offline-patch-catalog/Build-Catalog.ps1') -Algorithm SHA256).Hash){throw 'Patch date generator source differs.'}
}finally{$sourceArchive.Dispose()}
$exe=Join-Path $PackageDirectory 'DotaArchive.exe'
$version=(Get-Item -LiteralPath $exe).VersionInfo.FileVersion
if($version -ne $expectedVersion){throw 'Incorrect release file version.'}
$engineExe=Join-Path $PackageDirectory 'engine/DepotDownloader.exe'
if(!$manifest.EngineModified -or !$engineProvenance.Modified -or $manifest.EngineVersion -ne $engineProvenance.EngineVersion -or
    $manifest.EngineSha256 -ne $engineProvenance.EngineSha256 -or (Get-FileHash -LiteralPath $engineExe -Algorithm SHA256).Hash -ine $manifest.EngineSha256){throw 'Modified engine identity mismatch.'}
if(!$manifest.OfflinePatchLookup -or $manifest.PatchDateEntries -ne $patchDateProvenance.PatchCount -or $manifest.PatchDateCatalogSha256 -ne $patchDateProvenance.CatalogSha256 -or (Get-FileHash -LiteralPath (Join-Path $PackageDirectory 'data/patch-dates/patch-release-catalog.json') -Algorithm SHA256).Hash -ine $patchDateProvenance.CatalogSha256){throw 'Offline patch catalog identity mismatch.'}
$report=[ordered]@{
    Passed=$true;CheckedManifestFiles=$checked;ManifestMismatches=0;ZipEntriesVerified=$zipChecked;
    FrozenSourceFilesVerified=$sourceChecked;ModifiedEngineSourceFilesVerified=$engineSourceChecked;EngineSha256=$manifest.EngineSha256;DirectoryBytes=(Get-ChildItem -LiteralPath $PackageDirectory -File -Recurse | Measure-Object Length -Sum).Sum;
    ZipBytes=(Get-Item -LiteralPath $zipPath).Length;ZipSha256=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant();
    ExeSha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant();Version=$version;
    PatchDateEntries=$patchDateProvenance.PatchCount;PatchDateCatalogSha256=$patchDateProvenance.CatalogSha256;PatchDateGeneratorVerified=$true;
    NetworkUsed=$false;GameDownloaded=$false;ApplicationStarted=$false
}
$reportPath=Join-Path $releaseRoot ('DotaArchive-'+$manifest.Version+'-final-package-validation.json')
$report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $reportPath -Encoding UTF8
if(!$RebuildRoot){$RebuildRoot=Join-Path ([IO.Path]::GetTempPath()) ('dotaarchive-source-check-'+[Guid]::NewGuid().ToString('N'))}
$RebuildRoot=[IO.Path]::GetFullPath($RebuildRoot)
if($RebuildRoot -match '[^\x00-\x7F]' -or (Test-Path -LiteralPath $RebuildRoot)){throw 'Choose a new ASCII rebuild directory.'}
$sourceRoot=Join-Path $RebuildRoot 'source'
New-Item -ItemType Directory -Path $sourceRoot | Out-Null
[IO.Compression.ZipFile]::ExtractToDirectory($sourceZip,$sourceRoot)
& (Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe') -NoProfile -ExecutionPolicy Bypass -File (Join-Path $sourceRoot 'Rebuild.ps1') -ReleaseDirectory $PackageDirectory -OutputDirectory (Join-Path $RebuildRoot 'rebuilt')
if($LASTEXITCODE -ne 0){throw 'Offline source rebuilding failed.'}
$rebuiltVersion=(Get-Item -LiteralPath (Join-Path $RebuildRoot 'rebuilt/DotaArchive.exe')).VersionInfo.FileVersion
if($rebuiltVersion -ne $expectedVersion){throw 'Rebuilt version mismatch.'}
if((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ine $report.ExeSha256){throw 'Original release changed during rebuild.'}
$rebuildReport=[ordered]@{Passed=$true;Offline=$true;Shell='Windows PowerShell 5.1';Source=[IO.Path]::GetFileName($sourceZip);Version=$rebuiltVersion;SourceCommit=$manifest.EngineCommit;OriginalPackageModified=$false;RebuildDirectory=$RebuildRoot}
$rebuildReport | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $releaseRoot ('DotaArchive-'+$manifest.Version+'-source-rebuild-validation.json')) -Encoding UTF8
$report | ConvertTo-Json -Depth 4
$rebuildReport | ConvertTo-Json -Depth 4

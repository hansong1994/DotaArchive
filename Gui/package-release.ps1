param(
    [string]$Version='1.0.8',
    [string]$OutputRoot=(Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'outputs\releases'),
    [string]$SynchronizedCatalogDirectory,
    [string]$GuiAcceptancePath,
    [switch]$SkipZip
)
$ErrorActionPreference='Stop'
if($Version -notmatch '^\d+\.\d+\.\d+$'){throw 'Version must contain three numeric components.'}
$workspace=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$workRoot=Split-Path $PSScriptRoot -Parent
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
if($OutputRoot -match '[^\x00-\x7F]'){throw 'Release output paths must use ASCII characters.'}
if(!$SynchronizedCatalogDirectory){
    $candidates=@((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'DotaArchive\BrowserPrototype\latest-sync'),(Join-Path ([Environment]::GetFolderPath('UserProfile')) 'AppData\Local\DotaArchive\BrowserPrototype\latest-sync'))
    $SynchronizedCatalogDirectory=$candidates | Where-Object {Test-Path -LiteralPath (Join-Path $_ 'current.json')} | Select-Object -First 1
}
if(!$SynchronizedCatalogDirectory){throw 'Specify the committed latest-sync catalog with -SynchronizedCatalogDirectory.'}
$SynchronizedCatalogDirectory=[IO.Path]::GetFullPath($SynchronizedCatalogDirectory)
$name='DotaArchive-'+$Version+'-win-x64'
$destination=Join-Path $OutputRoot $name
$zipPath=Join-Path $OutputRoot ($name+'.zip')
if((Test-Path -LiteralPath $destination) -or (Test-Path -LiteralPath $zipPath)){throw 'Release output already exists. Choose a new OutputRoot; published packages are never overwritten.'}
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$stage=Join-Path $OutputRoot ('stage-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
$toolRoot=Join-Path $workRoot ('release-tools-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $stage,$toolRoot | Out-Null
$utf8=New-Object Text.UTF8Encoding($false)
function Write-Json([string]$Path,$Value){[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 12),$utf8)}
function Assert-Hash([string]$Path,[string]$Expected){if((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Expected){throw ('SHA256 mismatch: '+[IO.Path]::GetFileName($Path))}}
function Copy-File([string]$Source,[string]$Target){[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Target))|Out-Null; Copy-Item -LiteralPath $Source -Destination $Target}

# All binary dependency inputs are pinned and must already be local. Packaging
# never installs a runtime, starts a browser, authenticates or downloads a game.
foreach($dependency in @(@('microsoft.web.webview2.1.0.4258.31.nupkg','56f7f4b8bf9aee4b8efefbbdd4f67d5f74ebd1b100ed0806da71bf76af481aa9'),@('htmlagilitypack.1.13.1.nupkg','fc3f549e1c86eff1ee6e7ad872a7c0f5ac21cee093d0f606f8270b3e937afcce'))){Assert-Hash (Join-Path $PSScriptRoot ('dependencies\'+$dependency[0])) $dependency[1]}
& (Join-Path $PSScriptRoot 'Prepare-BrowserDependencies.ps1') -OutputDirectory $stage | Out-Null
& (Join-Path $PSScriptRoot 'release\New-ReleaseIcon.ps1') -OutputPath (Join-Path $stage 'assets\DotaArchive.ico') | Out-Null
$sources=@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'core') -Filter '*.cs' | ForEach-Object FullName)
$sources+=@(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | Where-Object {$_.Name -notmatch '(Tests?|Probe|Fixture|Verifier)\.cs$'} | ForEach-Object FullName)
$references=@('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll',('/r:'+(Join-Path $stage 'Microsoft.Web.WebView2.Core.dll')),('/r:'+(Join-Path $stage 'Microsoft.Web.WebView2.WinForms.dll')),('/r:'+(Join-Path $stage 'HtmlAgilityPack.dll')))
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$arguments=@('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output','/define:DOTA_ARCHIVE_RELEASE',('/win32manifest:'+(Join-Path $PSScriptRoot 'core\app.manifest')),('/win32icon:'+(Join-Path $stage 'assets\DotaArchive.ico')),('/out:'+(Join-Path $stage 'DotaArchive.exe')))+$references+$sources
& $compiler @arguments
if($LASTEXITCODE -ne 0){throw 'Release compilation failed.'}
if([Version](Get-Item -LiteralPath (Join-Path $stage 'DotaArchive.exe')).VersionInfo.FileVersion -ne [Version]($Version+'.0')){throw 'Compiled assembly version does not match the requested release version.'}
Copy-File (Join-Path $PSScriptRoot 'core\App.config') (Join-Path $stage 'DotaArchive.exe.config')
$engineRoot=Join-Path $workRoot 'depotdownloader-historical-engine'
$engine=Join-Path $engineRoot 'artifacts\win-x64\DepotDownloader.exe'
$engineSha256='e2eb9c272d257beaa158c8a1236cc66ec0ae134015ec9550a7d44de291f198ca'
Assert-Hash $engine $engineSha256
$engineBuild=Get-Content -LiteralPath (Join-Path $engineRoot 'build\build-result.json') -Raw | ConvertFrom-Json
if(!$engineBuild.Passed -or $engineBuild.EngineSha256 -ne $engineSha256 -or $engineBuild.Version -ne '3.4.0-history.1+c553ef4'){throw 'The modified engine build record does not match the pinned executable.'}
foreach($record in $engineBuild.Files){Assert-Hash (Join-Path $engineRoot ('DepotDownloader\'+$record.Path)) $record.Sha256}
Copy-File $engine (Join-Path $stage 'engine\DepotDownloader.exe')
Copy-File (Join-Path $engineRoot 'HISTORICAL-COMPATIBILITY.txt') (Join-Path $stage 'engine\HISTORICAL-COMPATIBILITY.txt')
Write-Json (Join-Path $stage 'engine\provenance.json') ([ordered]@{EngineVersion=$engineBuild.Version;EngineSha256=$engineSha256;Modified=$true;UpstreamCommit=$engineBuild.UpstreamCommit;SdkVersion=$engineBuild.SdkVersion;RuntimeVersion='9.0.21';SourceFiles=$engineBuild.Files;SteamAuthorizationChecksPreserved=$true})
Copy-File (Join-Path $PSScriptRoot 'core\LICENSE') (Join-Path $stage 'LICENSE.txt')
Copy-File (Join-Path $PSScriptRoot 'core\LICENSE') (Join-Path $stage 'licenses\DepotDownloader-LICENSE.txt')
Copy-File (Join-Path $PSScriptRoot 'release\HtmlAgilityPack-LICENSE.txt') (Join-Path $stage 'licenses\HtmlAgilityPack-LICENSE.txt')
foreach($file in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'release\third-party') -File | Where-Object {$_.Extension -in @('.txt','.nuspec') -or $_.Name -eq 'SteamKit-LICENSE'}){Copy-File $file.FullName (Join-Path $stage ('licenses\'+$file.Name))}
Copy-File (Join-Path $PSScriptRoot 'release\README.txt') (Join-Path $stage 'README.txt')
Copy-File (Join-Path $PSScriptRoot 'release\THIRD-PARTY-NOTICES.txt') (Join-Path $stage 'THIRD-PARTY-NOTICES.txt')

# Compile the export tool against the exact same parser/replay implementation.
$toolSources=@($sources)
$toolSources+=Join-Path $PSScriptRoot 'release\ReleaseDataTool.cs'
$toolArguments=@('/nologo','/target:exe','/platform:x64','/optimize+','/utf8output','/main:DotaArchive.ReleaseDataTool',('/out:'+(Join-Path $toolRoot 'ReleaseDataTool.exe')))+$references+$toolSources
& $compiler @toolArguments
if($LASTEXITCODE -ne 0){throw 'Offline export tool compilation failed.'}
foreach($dll in Get-ChildItem -LiteralPath $stage -Filter '*.dll'){Copy-Item -LiteralPath $dll.FullName -Destination $toolRoot}
Copy-File (Join-Path $PSScriptRoot 'core\App.config') (Join-Path $toolRoot 'ReleaseDataTool.exe.config')
$inputRoot=Join-Path $toolRoot 'input'
& (Join-Path $PSScriptRoot 'Prepare-LocalArchive.ps1') -OutputDirectory $inputRoot | Out-Null
& (Join-Path $PSScriptRoot 'tests\local-patch-evidence\Import-LocalPatchEvidence.ps1') -OutputDirectory (Join-Path $inputRoot 'data\patch-evidence') | Out-Null
& (Join-Path $PSScriptRoot 'Prepare-LocalPatchDates.ps1') -OutputDirectory (Join-Path $inputRoot 'data\patch-dates') | Out-Null
$patchUser=Join-Path (Split-Path $SynchronizedCatalogDirectory -Parent) 'patch-evidence'
$dataResult=& (Join-Path $toolRoot 'ReleaseDataTool.exe') export (Join-Path $inputRoot 'data\history') $SynchronizedCatalogDirectory (Join-Path $inputRoot 'data\patch-evidence') $patchUser (Join-Path $inputRoot 'data\patch-dates') (Join-Path $stage 'data')
if($LASTEXITCODE -ne 0){throw 'Public catalog export or offline validation failed.'}
New-Item -ItemType Directory -Path (Join-Path $stage 'verification') | Out-Null
[IO.File]::WriteAllText((Join-Path $stage 'verification\catalog-validation.json'),($dataResult -join [Environment]::NewLine),$utf8)
if(!$GuiAcceptancePath){$GuiAcceptancePath=Join-Path $workRoot 'patch-direction-smoke\gui-acceptance.json'}
$acceptance=Get-Content -LiteralPath $GuiAcceptancePath -Raw | ConvertFrom-Json
if($acceptance.Passed -ne $true -or $acceptance.Version -ne ($Version+'.0') -or $acceptance.DirectionContract -ne 'StrictBeforeAfter' -or $acceptance.Downloaded -ne $false -or $acceptance.DiagnosticProcessClosed -ne $true -or @($acceptance.Queries).Count -lt 4){throw 'Current-release directional GUI query acceptance record is incomplete or failed.'}
$directions=@($acceptance.Queries | ForEach-Object { $_.Direction } | Select-Object -Unique)
if($directions.Count -ne 2 -or 'Before' -notin $directions -or 'After' -notin $directions){throw 'Directional GUI acceptance must cover both Before and After.'}
$guiQueries=@()
foreach($query in $acceptance.Queries){
    if($query.LocalHit -ne $true -or $query.BrowserInitialized -ne $false -or $query.Complete -ne $true -or $query.Depots -le 0 -or [string]::IsNullOrWhiteSpace($query.Patch) -or [string]::IsNullOrWhiteSpace($query.BuildId)){throw 'Directional GUI query did not complete locally.'}
    $anchor=[DateTimeOffset]::Parse($query.AnchorUtc,[Globalization.CultureInfo]::InvariantCulture)
    $published=[DateTimeOffset]::Parse($query.PublishedAtUtc,[Globalization.CultureInfo]::InvariantCulture)
    if(($query.Direction -eq 'Before' -and $published -ge $anchor) -or ($query.Direction -eq 'After' -and $published -le $anchor)){throw 'Directional GUI query did not strictly honor its reference instant.'}
    $guiQueries += [ordered]@{Patch=$query.Patch;Direction=$query.Direction;AnchorUtc=$anchor.ToString('o');BuildId=$query.BuildId;PublishedAtUtc=$published.ToString('o');Depots=$query.Depots;Complete=$true;LocalHit=$true;BrowserInitialized=$false}
}
Write-Json (Join-Path $stage 'verification\gui-offline-patch-direction.json') ([ordered]@{Passed=$true;Version=$Version+'.0';DirectionContract='StrictBeforeAfter';Queries=$guiQueries;Downloaded=$false;DiagnosticProcessClosed=$true;CheckedAtUtc=$acceptance.CheckedAtUtc;Scope='Current-release offline GUI patch direction queries only; no download, launch or local game verification.'})
# Retain a public, minimal validation summary. Raw captures, client paths and
# account state remain in the maintainer workspace, never in the release.
$icuTestPath=Join-Path $PSScriptRoot 'tests\historical-icu\results.json'
$icuTests=Get-Content -LiteralPath $icuTestPath -Raw | ConvertFrom-Json
if($icuTests.Passed -ne $true -or $icuTests.Assertions -le 0 -or @($icuTests.Cases).Count -eq 0 -or @($icuTests.Cases | Where-Object {$_.Passed -ne $true}).Count -ne 0 -or $icuTests.SteamRequests -ne 0 -or $icuTests.GameLaunches -ne 0 -or $icuTests.HostClientWrites -ne 0){throw 'Historical ICU isolated regression checks have not passed.'}
$icuCases=@($icuTests.Cases | ForEach-Object {[ordered]@{Name=$_.Name;Passed=$_.Passed;Assertions=$_.Assertions}})
Write-Json (Join-Path $stage 'verification\historical-icu-regression.json') ([ordered]@{Passed=$true;Assertions=$icuTests.Assertions;Cases=$icuCases;CompletedAtUtc=$icuTests.CompletedAtUtc;SteamRequests=0;GameLaunches=0;HostClientWrites=0;Scope='Isolated copies of original ICU 52 PE files; deterministic import repair, original backups, repeated installation, restoration and refusal checks.'})
$runtimePath=Join-Path $workRoot 'client701-diagnostics\runtime-validation.json'
$runtime=Get-Content -LiteralPath $runtimePath -Raw | ConvertFrom-Json
if($runtime.Passed -ne $true -or $runtime.BuildId -ne '1532815' -or $runtime.GameplayFilesModified -ne $false -or $runtime.SystemFilesModified -ne $false -or $runtime.MenuVerified -isnot [bool] -or $runtime.LocalGameVerified -isnot [bool]){throw 'Build 1532815 runtime validation summary is incomplete or failed.'}
foreach($field in @('LocalMapLoaded','BotSimulationStarted','MovementVerified','AbilityCastVerified','AwaitingManualFirewallDismissal','PostRepairManifestVerificationPassed')){if($runtime.$field -isnot [bool]){throw ('Runtime validation must state '+$field+' explicitly.')}}
$postRepairPath=Join-Path $workRoot 'client701-diagnostics\post-repair-file-verification.json'
$postRepair=Get-Content -LiteralPath $postRepairPath -Raw | ConvertFrom-Json
if($postRepair.BuildId -ne $runtime.BuildId -or !$postRepair.Result.Success -or $postRepair.Result.CheckedFiles -ne $runtime.CheckedFiles -or !$runtime.PostRepairManifestVerificationPassed){throw 'Runtime evidence and post-repair manifest verification disagree.'}
Write-Json (Join-Path $stage 'verification\client701-runtime-validation.json') ([ordered]@{Passed=$runtime.Passed;BuildId=$runtime.BuildId;MenuVerified=$runtime.MenuVerified;LocalGameVerified=$runtime.LocalGameVerified;LocalMapLoaded=$runtime.LocalMapLoaded;BotSimulationStarted=$runtime.BotSimulationStarted;MovementVerified=$runtime.MovementVerified;AbilityCastVerified=$runtime.AbilityCastVerified;AwaitingManualFirewallDismissal=$runtime.AwaitingManualFirewallDismissal;GameplayFilesModified=$false;SystemFilesModified=$false;CheckedFiles=$runtime.CheckedFiles;ManifestCount=$runtime.ManifestCount;PostRepairManifestVerificationPassed=$runtime.PostRepairManifestVerificationPassed;CheckedAtUtc=$runtime.CheckedAtUtc;Summary=$runtime.Summary;EvidenceSha256=(Get-FileHash -LiteralPath $runtimePath -Algorithm SHA256).Hash.ToLowerInvariant();EvidenceRelease='1.0.6';Scope='Retained 1.0.6 runtime observations for Build 1532815 only; not a fresh 1.0.8 game test, and does not certify every historical client.'})
Write-Json (Join-Path $stage 'verification\client701-file-verification.json') ([ordered]@{Passed=$true;BuildId=$postRepair.BuildId;ManifestCount=$postRepair.ManifestCount;CheckedAtUtc=$postRepair.CheckedAtUtc;CheckedFiles=$postRepair.Result.CheckedFiles;FailedFiles=$postRepair.Result.FailedFiles;VerifiedBytes=$postRepair.Result.VerifiedBytes;UsesCompatibility=$postRepair.Result.UsesCompatibility;OriginalBackupsAndPrivateCopyVerified=$true;EvidenceSha256=(Get-FileHash -LiteralPath $postRepairPath -Algorithm SHA256).Hash.ToLowerInvariant()})
$savedResume=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'tests\saved-resume\results.json') -Raw | ConvertFrom-Json
$asciiLaunch=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'tests\ascii-launch\results.json') -Raw | ConvertFrom-Json
if($savedResume.Passed -le 0 -or $savedResume.Passed -ne @($savedResume.Cases).Count -or $savedResume.NetworkUsed -or $asciiLaunch.Success -ne $true -or @($asciiLaunch.Checks | Where-Object {$_.Passed -ne $true}).Count -ne 0){throw 'Saved-download or ASCII-launch regression did not pass.'}
Write-Json (Join-Path $stage 'verification\saved-resume-regression.json') ([ordered]@{Passed=$true;Assertions=$savedResume.Passed;Cases=$savedResume.Cases;ActualClientReadOnly=$savedResume.ReadOnlyActualClient;NetworkUsed=$false})
Write-Json (Join-Path $stage 'verification\ascii-launch-regression.json') ([ordered]@{Passed=$true;Assertions=@($asciiLaunch.Checks).Count;Cases=@($asciiLaunch.Checks | ForEach-Object {[ordered]@{Name=$_.Name;Passed=$_.Passed}});Scope='Offline inert files and NTFS junctions only; no game process, network or actual client writes.'})

# Complete corresponding source and practical offline GUI rebuild entry point.
$sourceRoot=Join-Path $toolRoot 'source'; $sourceGui=Join-Path $sourceRoot 'Gui'
New-Item -ItemType Directory -Force -Path $sourceGui | Out-Null
foreach($file in Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object {$_.Extension -in @('.cs','.ps1')}){Copy-File $file.FullName (Join-Path $sourceGui $file.Name)}
foreach($folder in @('core','release','tests')){
    $folderPath=Join-Path $PSScriptRoot $folder
    foreach($file in Get-ChildItem -LiteralPath $folderPath -File -Recurse | Where-Object {$_.Extension -in @('.cs','.ps1') -or ($folder -eq 'release' -and $_.Extension -in @('.txt','.nuspec')) -or ($folder -eq 'core' -and $_.Name -in @('App.config','app.manifest','LICENSE'))}){
        $relative=$file.FullName.Substring($PSScriptRoot.Length+1)
        Copy-File $file.FullName (Join-Path $sourceGui $relative)
    }
}
Copy-File (Join-Path $PSScriptRoot 'release\README.txt') (Join-Path $sourceGui 'README.txt')
Copy-File (Join-Path $PSScriptRoot 'release\Rebuild.ps1') (Join-Path $sourceRoot 'Rebuild.ps1')
Copy-File (Join-Path $PSScriptRoot 'release\SOURCE-README.txt') (Join-Path $sourceRoot 'README.txt')
Copy-File (Join-Path $PSScriptRoot 'core\LICENSE') (Join-Path $sourceRoot 'LICENSE.txt')
Copy-File (Join-Path $workRoot 'offline-patch-catalog\Build-Catalog.ps1') (Join-Path $sourceRoot 'PatchDateCatalog\Build-Catalog.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$engineSource=Join-Path $workRoot 'DepotDownloader-source-c553ef4.zip'
Assert-Hash $engineSource 'a0d3f943977454590391219773db4bb9c2dff98d4259831091bc448bf08f0bf4'
$engineZip=[IO.Compression.ZipFile]::OpenRead($engineSource)
try {
    foreach($entry in $engineZip.Entries | Where-Object {$_.Name}){
        $relative=($entry.FullName -split '/',2)[1]
        if($relative -match '(^|/)\.\.(/|$)' -or [IO.Path]::IsPathRooted($relative)){throw 'Unexpected source archive path.'}
        $target=Join-Path (Join-Path $sourceRoot 'DepotDownloader') $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))|Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target)
    }
} finally {$engineZip.Dispose()}
# Overlay the exact modified sources, not the unrelated upstream-only tree.
# Copy no build output, NuGet configuration/cache, account state or diagnostics.
foreach($record in $engineBuild.Files){
    Copy-File (Join-Path $engineRoot ('DepotDownloader\'+$record.Path)) (Join-Path $sourceRoot ('DepotDownloader\DepotDownloader\'+$record.Path))
}
foreach($relative in @('HISTORICAL-COMPATIBILITY.txt','Build-HistoricalEngine.ps1','Tests\HistoricalDepotPolicyTests.cs','Tests\HistoricalDepotPolicyTests.csproj')){
    Copy-File (Join-Path $engineRoot $relative) (Join-Path $sourceRoot ('DepotDownloader\'+$relative))
}
Copy-File (Join-Path $PSScriptRoot 'release\third-party\SteamKit-source-c31ce16.zip') (Join-Path $sourceRoot 'SteamKit-3.2.0-source.zip')
New-Item -ItemType Directory -Path (Join-Path $stage 'source') | Out-Null
$sourceZip=Join-Path $stage ('source\DotaArchive-'+$Version+'-source.zip')
[IO.Compression.ZipFile]::CreateFromDirectory($sourceRoot,$sourceZip,[IO.Compression.CompressionLevel]::Optimal,$false)
Copy-File (Join-Path $PSScriptRoot 'release\SOURCE-README.txt') (Join-Path $stage 'source\README.txt')

$catalog=Get-Content -LiteralPath (Join-Path $stage 'data\catalog-provenance.json') -Raw | ConvertFrom-Json
$patchDates=Get-Content -LiteralPath (Join-Path $stage 'data\patch-dates\provenance.json') -Raw | ConvertFrom-Json
$privateFiles=@(Get-ChildItem -LiteralPath $stage -File -Recurse | Where-Object {$_.Name -match '^(Cookies|Login Data|AccountSettingsStore|ssfn|config\.vdf|loginusers\.vdf)|access.token|refresh.token|progress\.log|download\.log'})
if($privateFiles.Count){throw 'A private runtime/account file entered the release staging directory.'}
foreach($dataFile in Get-ChildItem -LiteralPath (Join-Path $stage 'data') -File -Recurse -Filter '*.json'){
    if([regex]::IsMatch([IO.File]::ReadAllText($dataFile.FullName),'steamLoginSecure|steamRememberLogin|g_steamID|data-userid|access_token|refresh_token|sessionid|openid\.sig',[Text.RegularExpressions.RegexOptions]::IgnoreCase)){throw ('Public data contains an unexpected login/session marker: '+$dataFile.Name)}
}
$files=@(Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName | ForEach-Object {[ordered]@{Path=$_.FullName.Substring($stage.Length+1).Replace('\','/');Bytes=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
Write-Json (Join-Path $stage 'release-manifest.json') ([ordered]@{
    SchemaVersion=1;Product='Dota Archive';Version=$Version;Platform='Windows x64';PackagedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');ReleaseCompilation=$true;
    EngineVersion='3.4.0-history.1+c553ef4';EngineModified=$true;EngineCommit='c553ef4d60c00a4f5fd16c9fe017f569001589ff';EngineRuntimeVersion='9.0.21';EngineSha256=(Get-FileHash -LiteralPath $engine -Algorithm SHA256).Hash.ToLowerInvariant();
    Requirements=@('Windows 10/11 x64','.NET Framework 4.8','Microsoft Edge WebView2 Evergreen Runtime');SilentRuntimeInstall=$false;
    ContinuousHistoryRecords=$catalog.TotalContinuousRecords;LatestBuildId=$catalog.LatestBuildId;VerifiedThroughUtc=$catalog.VerifiedThroughUtc;
    CredentialsIncluded=$false;GameFilesIncluded=$false;ManualVersionIndex=$false;PatchDateEntries=$patchDates.PatchCount;PatchDateCatalogSha256=$patchDates.CatalogSha256;OfflinePatchLookup=$true;PatchQueryDirection='StrictBeforeAfter';PatchDateWindowCutoff=$false;SourceArchive=('source/DotaArchive-'+$Version+'-source.zip');Files=$files
})
$hashLines=@(Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName | ForEach-Object {(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+$_.FullName.Substring($stage.Length+1).Replace('\','/')})
[IO.File]::WriteAllLines((Join-Path $stage 'SHA256SUMS.txt'),$hashLines,[Text.Encoding]::ASCII)
# Move only the freshly created and resolved staging directory inside OutputRoot.
$resolvedStage=[IO.Path]::GetFullPath($stage)
if(!(Test-Path -LiteralPath $resolvedStage) -or ![String]::Equals([IO.Path]::GetDirectoryName($resolvedStage),$OutputRoot,[StringComparison]::OrdinalIgnoreCase) -or !(Split-Path $resolvedStage -Leaf).StartsWith('stage-')){throw 'Invalid release staging boundary.'}
Move-Item -LiteralPath $resolvedStage -Destination $destination
$verification=& (Join-Path $toolRoot 'ReleaseDataTool.exe') verify (Join-Path $destination 'data')
if($LASTEXITCODE -ne 0){throw 'Relocated release data validation failed.'}
if(!$SkipZip){
    [IO.Compression.ZipFile]::CreateFromDirectory($destination,$zipPath,[IO.Compression.CompressionLevel]::Optimal,$true)
    $zipHash=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $OutputRoot ($name+'.sha256.txt')),($zipHash+'  '+[IO.Path]::GetFileName($zipPath)+[Environment]::NewLine),[Text.Encoding]::ASCII)
}
Write-Json (Join-Path $OutputRoot ($name+'-build-result.json')) ([ordered]@{PackageDirectory=$destination;Zip=if($SkipZip){$null}else{$zipPath};ZipSha256=if($SkipZip){$null}else{$zipHash};ExecutableSha256=(Get-FileHash -LiteralPath (Join-Path $destination 'DotaArchive.exe') -Algorithm SHA256).Hash.ToLowerInvariant();CatalogVerification=($verification -join [Environment]::NewLine|ConvertFrom-Json);BuildToolsDirectory=$toolRoot})
Write-Output $destination

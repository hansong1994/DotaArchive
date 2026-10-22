param([string]$CatalogFile,[string]$ArchiveDirectory,[string]$EvidenceDirectory,[string]$SyncArchiveDirectory)
$ErrorActionPreference='Stop'
$prototype=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$workRoot=Split-Path $prototype -Parent
if(!$CatalogFile){$CatalogFile=Join-Path $workRoot 'offline-patch-catalog/patch-release-catalog.json'}
if(!$ArchiveDirectory){$ArchiveDirectory=Join-Path $workRoot 'dota-history-archive-2013-2015/cache'}
if(!$EvidenceDirectory){$EvidenceDirectory=Join-Path $prototype 'tests/local-patch-evidence/bundle'}
if(!$SyncArchiveDirectory){$SyncArchiveDirectory=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'DotaArchive/BrowserPrototype/latest-sync'}
if(!(Test-Path -LiteralPath (Join-Path $SyncArchiveDirectory 'current.json'))){throw 'Specify a committed synchronized history catalog with -SyncArchiveDirectory.'}
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$dependency=Join-Path $prototype 'bin/HtmlAgilityPack.dll'
Copy-Item -LiteralPath $dependency -Destination (Join-Path $PSScriptRoot 'HtmlAgilityPack.dll') -Force
$sources=@('core/Models.cs','HistoryModels.cs','HistoryStateReducer.cs','SteamDbHistoryParser.cs','HistoryCacheStore.cs','HistoryAnchorStore.cs','DateFirstHistoryLocator.cs','OfficialPatchEvidence.cs','OnlineHistoryResolver.cs','SyncCatalogStore.cs','LocalArchiveResolver.cs','LocalPatchEvidenceStore.cs','LocalPatchDates.cs') | ForEach-Object {Join-Path $prototype $_}
$output=Join-Path $PSScriptRoot 'LocalPatchDateTests.exe'
& $compiler /nologo /target:exe /platform:x64 /utf8output /main:DotaArchive.LocalPatchDateTests ('/out:'+$output) /r:System.Web.Extensions.dll ('/r:'+$dependency) @sources (Join-Path $PSScriptRoot 'LocalPatchDateTests.cs')
if($LASTEXITCODE -ne 0){throw 'Local patch date tests failed to compile.'}
& $output $PSScriptRoot ([IO.Path]::GetFullPath($CatalogFile)) ([IO.Path]::GetFullPath($ArchiveDirectory)) ([IO.Path]::GetFullPath($EvidenceDirectory)) ([IO.Path]::GetFullPath($SyncArchiveDirectory))
if($LASTEXITCODE -ne 0){throw 'Local patch date tests failed.'}

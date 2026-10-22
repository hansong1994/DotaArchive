param([string]$ArchiveDirectory)
$ErrorActionPreference='Stop'
$prototype=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if(!$ArchiveDirectory){$ArchiveDirectory=Join-Path (Split-Path $prototype -Parent) 'dota-history-archive-2013-2015/cache'}
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$dependency=Join-Path $prototype 'bin/HtmlAgilityPack.dll'
Copy-Item -LiteralPath $dependency -Destination (Join-Path $PSScriptRoot 'HtmlAgilityPack.dll') -Force
$sources=@('core/Models.cs','HistoryModels.cs','HistoryStateReducer.cs','SteamDbHistoryParser.cs','HistoryCacheStore.cs','HistoryAnchorStore.cs','DateFirstHistoryLocator.cs','OfficialPatchEvidence.cs','OnlineHistoryResolver.cs','LocalArchiveResolver.cs','SyncCatalogStore.cs','HistoryLatestSync.cs') | ForEach-Object {Join-Path $prototype $_}
$output=Join-Path $PSScriptRoot 'HistoryLatestSyncTests.exe'
& $compiler /nologo /target:exe /platform:x64 /utf8output /main:DotaArchive.HistoryLatestSyncTests ('/out:'+$output) /r:System.Web.Extensions.dll ('/r:'+$dependency) @sources (Join-Path $PSScriptRoot 'HistoryLatestSyncTests.cs')
if($LASTEXITCODE -ne 0){throw 'Latest sync tests failed to compile.'}
& $output $PSScriptRoot ([IO.Path]::GetFullPath($ArchiveDirectory))
if($LASTEXITCODE -ne 0){throw 'Latest sync tests failed.'}

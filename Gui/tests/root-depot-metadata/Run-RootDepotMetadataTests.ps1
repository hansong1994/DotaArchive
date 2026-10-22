$ErrorActionPreference='Stop'
$prototypeRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$sources=@('core/Models.cs','HistoryModels.cs','HistoryStateReducer.cs','SteamDbHistoryParser.cs','HistoryCacheStore.cs','HistoryAnchorStore.cs','DateFirstHistoryLocator.cs','OfficialPatchEvidence.cs','OnlineHistoryResolver.cs','HistoryRangeArchive.cs') | ForEach-Object {Join-Path $prototypeRoot $_}
$sources+=Join-Path $PSScriptRoot 'RootDepotMetadataTests.cs'
$dependency=Join-Path $prototypeRoot 'bin/HtmlAgilityPack.dll'
Copy-Item -LiteralPath $dependency -Destination (Join-Path $PSScriptRoot 'HtmlAgilityPack.dll') -Force
$output=Join-Path $PSScriptRoot 'RootDepotMetadataTests.exe'
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') /nologo /target:exe /platform:x64 /utf8output /main:DotaArchive.RootDepotMetadataTests ('/out:'+$output) /r:System.Web.Extensions.dll ('/r:'+$dependency) @sources
if($LASTEXITCODE -ne 0){throw 'Root Depot metadata tests compile failed'}
$workspaceRoot=[IO.Path]::GetFullPath((Join-Path $prototypeRoot '../..'))
& $output $PSScriptRoot (Join-Path $workspaceRoot 'work/dota-history-archive-2013-2015/cache') (Join-Path $workspaceRoot 'work/dota-history-archive-2013-2015/run-20261021-resume2/evidence/latest-history.json')
if($LASTEXITCODE -ne 0){throw 'Root Depot metadata tests failed'}

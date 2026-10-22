$ErrorActionPreference='Stop'
$prototypeRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$workRoot=Split-Path $prototypeRoot -Parent
$bundle=Join-Path $PSScriptRoot 'bundle'
& (Join-Path $PSScriptRoot 'Import-LocalPatchEvidence.ps1') -OutputDirectory $bundle
$sources=@('core/Models.cs','HistoryModels.cs','HistoryStateReducer.cs','SteamDbHistoryParser.cs','HistoryCacheStore.cs','HistoryAnchorStore.cs','DateFirstHistoryLocator.cs','OfficialPatchEvidence.cs','OnlineHistoryResolver.cs','SyncCatalogStore.cs','LocalArchiveResolver.cs','LocalPatchEvidenceStore.cs') | ForEach-Object {Join-Path $prototypeRoot $_}
$sources+=Join-Path $PSScriptRoot 'LocalPatchEvidenceTests.cs'
$dependency=Join-Path $prototypeRoot 'bin/HtmlAgilityPack.dll'
Copy-Item -LiteralPath $dependency -Destination (Join-Path $PSScriptRoot 'HtmlAgilityPack.dll') -Force
$output=Join-Path $PSScriptRoot 'LocalPatchEvidenceTests.exe'
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') /nologo /target:exe /platform:x64 /utf8output /main:DotaArchive.LocalPatchEvidenceTests ('/out:'+$output) /r:System.Web.Extensions.dll ('/r:'+$dependency) @sources
if($LASTEXITCODE -ne 0){throw 'Local patch evidence tests compile failed'}
& $output $bundle (Join-Path $workRoot 'dota-history-archive-2013-2015/cache') (Join-Path $workRoot ('lpe-test-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')))
if($LASTEXITCODE -ne 0){throw 'Local patch evidence tests failed'}

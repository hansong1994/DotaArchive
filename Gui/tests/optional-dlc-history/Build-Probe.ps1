$ErrorActionPreference='Stop'
$prototypeRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$sources=@('core/Models.cs','HistoryModels.cs','HistoryStateReducer.cs','SteamDbHistoryParser.cs','HistoryCacheStore.cs') | ForEach-Object {Join-Path $prototypeRoot $_}
$sources+=Join-Path $PSScriptRoot 'OptionalDlcHistoryProbe.cs'
$dependency=Join-Path $prototypeRoot 'bin/HtmlAgilityPack.dll'
Copy-Item -LiteralPath $dependency -Destination (Join-Path $PSScriptRoot 'HtmlAgilityPack.dll') -Force
$output=Join-Path $PSScriptRoot 'OptionalDlcHistoryProbe.exe'
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') /nologo /target:exe /platform:x64 /utf8output ('/out:'+$output) /r:System.Web.Extensions.dll ('/r:'+$dependency) @sources
if($LASTEXITCODE -ne 0){throw 'Probe compile failed'}
$workspaceRoot=[IO.Path]::GetFullPath((Join-Path $prototypeRoot '../..'))
& $output (Join-Path $workspaceRoot 'work/dota-history-archive-2013-2015/cache/verified-public-history-v1.json') (Join-Path $workspaceRoot 'work/dota-history-archive-2013-2015/run-20261021-resume1/evidence/latest-history.json')

$ErrorActionPreference = 'Stop'
$prototypeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources = @('core/Models.cs', 'HistoryModels.cs', 'HistoryStateReducer.cs', 'SteamDbHistoryParser.cs', 'HistoryCacheStore.cs', 'HistoryAnchorStore.cs', 'DateFirstHistoryLocator.cs', 'OfficialPatchEvidence.cs', 'OnlineHistoryResolver.cs', 'HistoryRangeArchive.cs', 'DiagnosticHistoryBrowser.cs') | ForEach-Object { Join-Path $prototypeRoot $_ }
$sources += Join-Path $PSScriptRoot 'HistoryArchiveTests.cs'
$dependency = Join-Path $prototypeRoot 'bin/HtmlAgilityPack.dll'
Copy-Item -LiteralPath $dependency -Destination (Join-Path $PSScriptRoot 'HtmlAgilityPack.dll') -Force
$output = Join-Path $PSScriptRoot 'HistoryArchiveTests.exe'
& $compiler /nologo /target:exe /platform:x64 /utf8output /main:DotaArchive.HistoryArchiveTests ('/out:' + $output) /r:System.Web.Extensions.dll ('/r:' + $dependency) @sources
if ($LASTEXITCODE -ne 0) { throw 'History archive tests did not compile.' }
& $output $PSScriptRoot
if ($LASTEXITCODE -ne 0) { throw 'History archive tests failed.' }

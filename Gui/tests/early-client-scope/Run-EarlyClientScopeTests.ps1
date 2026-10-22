param([string]$ArchiveDirectory, [switch]$ScanOnly)
$ErrorActionPreference='Stop'
$prototype=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if(!$ArchiveDirectory){$ArchiveDirectory=Join-Path ([IO.Path]::GetFullPath((Join-Path $prototype '../..'))) 'outputs/releases/DotaArchive-1.0.1-win-x64/data/latest-sync'}
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$dependency=Join-Path $prototype 'bin/HtmlAgilityPack.dll'
Copy-Item -LiteralPath $dependency -Destination (Join-Path $PSScriptRoot 'HtmlAgilityPack.dll') -Force
$sources=@('core/Models.cs','HistoryModels.cs','HistoryStateReducer.cs','SteamDbHistoryParser.cs','HistoryCacheStore.cs','HistoryAnchorStore.cs','DateFirstHistoryLocator.cs','OfficialPatchEvidence.cs','OnlineHistoryResolver.cs','SyncCatalogStore.cs','LocalArchiveResolver.cs') | ForEach-Object {Join-Path $prototype $_}
$output=Join-Path $PSScriptRoot 'EarlyClientScopeTests.exe'
& $compiler /nologo /target:exe /platform:x64 /utf8output /main:DotaArchive.EarlyClientScopeTests ('/out:'+$output) /r:System.Web.Extensions.dll ('/r:'+$dependency) @sources (Join-Path $PSScriptRoot 'EarlyClientScopeTests.cs')
if($LASTEXITCODE -ne 0){throw 'Early client scope tests failed to compile.'}
& $output $PSScriptRoot ([IO.Path]::GetFullPath($ArchiveDirectory)) $ScanOnly.IsPresent
if($LASTEXITCODE -ne 0){throw 'Early client scope tests failed.'}

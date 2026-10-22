param([string]$OutputDirectory=(Join-Path $PSScriptRoot 'bin-history-archive'))
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'Prepare-BrowserDependencies.ps1') -OutputDirectory $OutputDirectory | Out-Null
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$names=@('HistoryModels.cs','HistoryStateReducer.cs','HistoryCacheStore.cs','HistoryAnchorStore.cs','SteamDbHistoryParser.cs','OfficialPatchEvidence.cs','DateFirstHistoryLocator.cs','OnlineHistoryResolver.cs','HistoryRangeArchive.cs','BrowserSession.cs','DiagnosticHistoryBrowser.cs','HistoryArchiveProbe.cs')
$sources=@((Join-Path $PSScriptRoot 'core/Models.cs')) + @($names | ForEach-Object {Join-Path $PSScriptRoot $_})
$arguments=@('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output','/main:DotaArchive.HistoryArchiveProgram','/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll',('/r:'+(Join-Path $OutputDirectory 'Microsoft.Web.WebView2.Core.dll')),('/r:'+(Join-Path $OutputDirectory 'Microsoft.Web.WebView2.WinForms.dll')),('/r:'+(Join-Path $OutputDirectory 'HtmlAgilityPack.dll')),('/win32manifest:'+(Join-Path $PSScriptRoot 'core/app.manifest')),('/out:'+(Join-Path $OutputDirectory 'DotaArchive.HistoryArchive.exe')))+$sources
& $compiler @arguments
if($LASTEXITCODE -ne 0){throw '历史归档窗口编译失败。'}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'core/App.config') -Destination (Join-Path $OutputDirectory 'DotaArchive.HistoryArchive.exe.config') -Force
Write-Output (Join-Path $OutputDirectory 'DotaArchive.HistoryArchive.exe')

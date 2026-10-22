param([string]$OutputDirectory=(Join-Path $PSScriptRoot 'bin'))
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'Prepare-BrowserDependencies.ps1') -OutputDirectory $OutputDirectory | Out-Null
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$models=Join-Path $PSScriptRoot 'core/Models.cs'
$sources=@('HistoryModels.cs','HistoryStateReducer.cs','BrowserSession.cs','BrowserProbe.cs') | ForEach-Object {Join-Path $PSScriptRoot $_}
$args=@('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output','/main:DotaArchive.BrowserProbeProgram','/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll',('/r:'+(Join-Path $OutputDirectory 'Microsoft.Web.WebView2.Core.dll')),('/r:'+(Join-Path $OutputDirectory 'Microsoft.Web.WebView2.WinForms.dll')),('/win32manifest:'+(Join-Path $PSScriptRoot 'core/app.manifest')),('/out:'+(Join-Path $OutputDirectory 'DotaArchive.BrowserProbe.exe')),$models)+$sources
& $compiler @args
if($LASTEXITCODE -ne 0){throw '浏览器实验窗口编译失败。'}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'core/App.config') -Destination (Join-Path $OutputDirectory 'DotaArchive.BrowserProbe.exe.config') -Force
Write-Output (Join-Path $OutputDirectory 'DotaArchive.BrowserProbe.exe')

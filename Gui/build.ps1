param([string]$OutputDirectory=(Join-Path $PSScriptRoot 'bin'))
$ErrorActionPreference='Stop'
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
& (Join-Path $PSScriptRoot 'Prepare-BrowserDependencies.ps1') -OutputDirectory $OutputDirectory
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sourceFiles=@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'core') -Filter '*.cs' | ForEach-Object FullName)
$sourceFiles+=@(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | Where-Object { $_.Name -notmatch '(Tests?|Probe|Fixture|Verifier)\.cs$' } | ForEach-Object FullName)
$arguments=@('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output','/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll',('/r:'+(Join-Path $OutputDirectory 'Microsoft.Web.WebView2.Core.dll')),('/r:'+(Join-Path $OutputDirectory 'Microsoft.Web.WebView2.WinForms.dll')),('/r:'+(Join-Path $OutputDirectory 'HtmlAgilityPack.dll')),('/win32manifest:'+(Join-Path $PSScriptRoot 'core\app.manifest')),('/out:'+(Join-Path $OutputDirectory 'DotaArchive.Prototype.exe')))+$sourceFiles
& $compiler @arguments
if($LASTEXITCODE -ne 0){throw '原型编译失败。'}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'core\App.config') -Destination (Join-Path $OutputDirectory 'DotaArchive.Prototype.exe.config') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'core\LICENSE') -Destination (Join-Path $OutputDirectory 'licenses\DepotDownloader-LICENSE') -Force
$engineSource=Join-Path (Split-Path $PSScriptRoot -Parent) 'depotdownloader-historical-engine\artifacts\win-x64\DepotDownloader.exe'
$engineSha256='e2eb9c272d257beaa158c8a1236cc66ec0ae134015ec9550a7d44de291f198ca'
if((Get-FileHash -LiteralPath $engineSource -Algorithm SHA256).Hash -ine $engineSha256){throw '下载引擎校验失败。'}
$engineDirectory=Join-Path $OutputDirectory 'engine'
New-Item -ItemType Directory -Force -Path $engineDirectory | Out-Null
Copy-Item -LiteralPath $engineSource -Destination (Join-Path $engineDirectory 'DepotDownloader.exe') -Force
& (Join-Path $PSScriptRoot 'Prepare-LocalArchive.ps1') -OutputDirectory $OutputDirectory
& (Join-Path $PSScriptRoot 'tests\local-patch-evidence\Import-LocalPatchEvidence.ps1') -OutputDirectory (Join-Path $OutputDirectory 'data\patch-evidence')
& (Join-Path $PSScriptRoot 'Prepare-LocalPatchDates.ps1') -OutputDirectory (Join-Path $OutputDirectory 'data\patch-dates')
@{BuiltAtUtc=[DateTime]::UtcNow.ToString('o');Exe=(Join-Path $OutputDirectory 'DotaArchive.Prototype.exe');ExeSha256=(Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'DotaArchive.Prototype.exe') -Algorithm SHA256).Hash.ToLowerInvariant();SourceFiles=$sourceFiles;CatalogDependency=$false;Prototype=$true} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'build-info.json') -Encoding UTF8
Write-Output (Join-Path $OutputDirectory 'DotaArchive.Prototype.exe')

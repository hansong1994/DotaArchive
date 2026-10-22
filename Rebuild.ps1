param(
    [Parameter(Mandatory=$true)][string]$ReleaseDirectory,
    [string]$OutputDirectory=(Join-Path $PSScriptRoot 'rebuilt')
)
$ErrorActionPreference='Stop'
$ReleaseDirectory=[IO.Path]::GetFullPath($ReleaseDirectory)
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$gui=Join-Path $PSScriptRoot 'Gui'
if(!(Test-Path -LiteralPath $gui)){throw 'Extract the complete source zip before running Rebuild.ps1.'}
if(Test-Path -LiteralPath $OutputDirectory){throw 'Choose an output directory that does not already exist.'}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
foreach($name in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','WebView2Loader.dll','HtmlAgilityPack.dll')){
    Copy-Item -LiteralPath (Join-Path $ReleaseDirectory $name) -Destination $OutputDirectory
}
foreach($name in @('data','engine','licenses','assets')){Copy-Item -LiteralPath (Join-Path $ReleaseDirectory $name) -Destination $OutputDirectory -Recurse}
$sources=@(Get-ChildItem -LiteralPath (Join-Path $gui 'core') -Filter '*.cs' | ForEach-Object FullName)
$sources+=@(Get-ChildItem -LiteralPath $gui -Filter '*.cs' | Where-Object {$_.Name -notmatch '(Tests?|Probe|Fixture|Verifier)\.cs$'} | ForEach-Object FullName)
$arguments=@('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output','/define:DOTA_ARCHIVE_RELEASE','/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll',('/r:'+(Join-Path $OutputDirectory 'Microsoft.Web.WebView2.Core.dll')),('/r:'+(Join-Path $OutputDirectory 'Microsoft.Web.WebView2.WinForms.dll')),('/r:'+(Join-Path $OutputDirectory 'HtmlAgilityPack.dll')),('/win32manifest:'+(Join-Path $gui 'core\app.manifest')),('/win32icon:'+(Join-Path $OutputDirectory 'assets\DotaArchive.ico')),('/out:'+(Join-Path $OutputDirectory 'DotaArchive.exe')))+$sources
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') @arguments
if($LASTEXITCODE -ne 0){throw 'Compilation failed.'}
Copy-Item -LiteralPath (Join-Path $gui 'core\App.config') -Destination (Join-Path $OutputDirectory 'DotaArchive.exe.config')
Write-Output (Join-Path $OutputDirectory 'DotaArchive.exe')

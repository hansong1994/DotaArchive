$ErrorActionPreference = 'Stop'
$prototypeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $prototypeRoot 'core') -Filter '*.cs' | ForEach-Object FullName)
$sources += @(Get-ChildItem -LiteralPath $prototypeRoot -Filter '*.cs' | Where-Object { $_.Name -notmatch '(Tests?|Probe|Fixture|Verifier)\.cs$' } | ForEach-Object FullName)
$sources += Join-Path $PSScriptRoot 'DownloadGuardTests.cs'
$references = @('/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll')
foreach ($dependency in @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'HtmlAgilityPack.dll')) {
    $existing = Join-Path $prototypeRoot ('bin/' + $dependency)
    $references += '/r:' + $existing
    Copy-Item -LiteralPath $existing -Destination (Join-Path $PSScriptRoot $dependency) -Force
}
$output = Join-Path $PSScriptRoot 'DownloadGuardTests.exe'
& $compiler /nologo /target:exe /platform:x64 /utf8output /main:DotaArchive.DownloadGuardTests ('/out:' + $output) @references @sources
if ($LASTEXITCODE -ne 0) { throw 'Download guard tests did not compile.' }
& $output
if ($LASTEXITCODE -ne 0) { throw 'Download guard tests failed.' }

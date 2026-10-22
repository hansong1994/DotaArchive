$ErrorActionPreference = 'Stop'
$prototypeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $prototypeRoot 'core') -Filter '*.cs' | ForEach-Object FullName)
$sources += @(Get-ChildItem -LiteralPath $prototypeRoot -Filter '*.cs' | Where-Object { $_.Name -notmatch '(Tests?|Probe|Fixture|Verifier)\.cs$' } | ForEach-Object FullName)
$sources += Join-Path $PSScriptRoot 'SavedResumeTests.cs'
$references = @('/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll')
foreach ($dependency in @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'HtmlAgilityPack.dll')) {
    $existing = Join-Path $prototypeRoot ('bin/' + $dependency)
    $references += '/r:' + $existing
    Copy-Item -LiteralPath $existing -Destination (Join-Path $PSScriptRoot $dependency) -Force
}
$output = Join-Path $PSScriptRoot 'SavedResumeTests.exe'
& $compiler /nologo /target:exe /platform:x64 /utf8output /main:DotaArchive.SavedResumeTests ('/out:' + $output) @references @sources
if ($LASTEXITCODE -ne 0) { throw 'Saved resume tests did not compile.' }
& $output $PSScriptRoot
if ($LASTEXITCODE -ne 0) { throw 'Saved resume tests failed.' }

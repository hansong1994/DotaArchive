$ErrorActionPreference='Stop'
$prototype=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources=@(Get-ChildItem -LiteralPath (Join-Path $prototype 'core') -Filter '*.cs' | ForEach-Object FullName)
$sources+=@(Get-ChildItem -LiteralPath $prototype -Filter '*.cs' | Where-Object { $_.Name -notmatch '(Tests?|Probe|Fixture|Verifier)\.cs$' } | ForEach-Object FullName)
$references=@('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll')
foreach($name in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','HtmlAgilityPack.dll')){
    $dependency=Join-Path $prototype ('bin/'+$name)
    $references+='/r:'+$dependency
    Copy-Item -LiteralPath $dependency -Destination (Join-Path $PSScriptRoot $name) -Force
}
$output=Join-Path $PSScriptRoot 'TimeDirectionUiTests.exe'
& $compiler /nologo /target:exe /platform:x64 /utf8output /define:DOTA_ARCHIVE_RELEASE /main:DotaArchive.TimeDirectionUiTests ('/out:'+$output) @references @sources (Join-Path $PSScriptRoot 'TimeDirectionUiTests.cs')
if($LASTEXITCODE -ne 0){throw 'Time direction UI tests failed to compile.'}
& $output $PSScriptRoot
if($LASTEXITCODE -ne 0){throw 'Time direction UI tests failed.'}

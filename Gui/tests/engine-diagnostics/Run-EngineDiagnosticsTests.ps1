$ErrorActionPreference='Stop'
$prototype=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources=@('Models.cs','VersionCatalog.cs','TimelineResolver.cs','CliOutputParser.cs','DownloadEngine.cs','QrRenderer.cs') | ForEach-Object {Join-Path $prototype ('core/'+$_)}
$output=Join-Path $PSScriptRoot 'EngineDiagnosticsTests.exe'
& $compiler /nologo /target:exe /platform:x64 /utf8output /r:System.Drawing.dll /r:System.Web.Extensions.dll ('/out:'+$output) (Join-Path $PSScriptRoot 'EngineDiagnosticsTests.cs') @sources
if($LASTEXITCODE -ne 0){throw 'Engine diagnostics tests failed to compile.'}
$shortFixtures=Join-Path ([IO.Path]::GetFullPath((Join-Path $prototype '../../../..'))) 'ed-tests'
& $output $PSScriptRoot $shortFixtures
if($LASTEXITCODE -ne 0){throw 'Engine diagnostics tests failed.'}

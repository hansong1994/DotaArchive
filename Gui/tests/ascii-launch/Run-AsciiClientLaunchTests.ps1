$ErrorActionPreference = 'Stop'
$prototypeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources = @((Join-Path $prototypeRoot 'core/Models.cs'), (Join-Path $prototypeRoot 'core/VersionCatalog.cs'), (Join-Path $prototypeRoot 'core/TimelineResolver.cs'), (Join-Path $prototypeRoot 'AsciiClientLaunch.cs'), (Join-Path $PSScriptRoot 'AsciiClientLaunchTests.cs'))
$output = Join-Path $PSScriptRoot 'AsciiClientLaunchTests.exe'
& $compiler /nologo /target:exe /platform:x64 /utf8output /r:System.Web.Extensions.dll ('/out:' + $output) @sources
if ($LASTEXITCODE -ne 0) { throw 'ASCII launch tests did not compile.' }
& $output $PSScriptRoot
if ($LASTEXITCODE -ne 0) { throw 'ASCII launch tests failed.' }

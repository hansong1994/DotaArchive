$ErrorActionPreference = 'Stop'
$prototypeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$output = Join-Path $PSScriptRoot 'SteamQrRefreshTests.exe'
& $compiler /nologo /target:exe /platform:x64 /utf8output /r:System.Web.Extensions.dll ('/out:' + $output) (Join-Path $prototypeRoot 'SteamQrRefreshState.cs') (Join-Path $PSScriptRoot 'SteamQrRefreshTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'QR refresh state tests did not compile.' }
& $output (Join-Path $PSScriptRoot 'results.json')
if ($LASTEXITCODE -ne 0) { throw 'QR refresh state tests failed.' }

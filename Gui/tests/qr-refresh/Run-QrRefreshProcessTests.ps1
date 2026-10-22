$ErrorActionPreference = 'Stop'
$prototypeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources = @('Models.cs', 'VersionCatalog.cs', 'TimelineResolver.cs', 'CliOutputParser.cs', 'DownloadEngine.cs', 'QrRenderer.cs') | ForEach-Object { Join-Path $prototypeRoot ('core/' + $_) }
$output = Join-Path $PSScriptRoot 'QrRefreshProcessTests.exe'
& $compiler /nologo /target:exe /platform:x64 /utf8output /r:System.Drawing.dll /r:System.Web.Extensions.dll ('/out:' + $output) (Join-Path $prototypeRoot 'SteamQrRefreshState.cs') (Join-Path $PSScriptRoot 'QrRefreshProcessTests.cs') @sources
if ($LASTEXITCODE -ne 0) { throw 'QR refresh process tests did not compile.' }
& $output (Join-Path $PSScriptRoot 'process-results.json')
if ($LASTEXITCODE -ne 0) { throw 'QR refresh process tests failed.' }

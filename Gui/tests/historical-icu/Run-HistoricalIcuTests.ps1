param([string]$FixtureDirectory)
$ErrorActionPreference='Stop'
$prototype=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if(!$FixtureDirectory){$FixtureDirectory=[IO.Path]::GetFullPath((Join-Path $prototype '../icu-compat-plan/originals'))}
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources=@('core/Models.cs','core/VersionCatalog.cs','core/TimelineResolver.cs','core/ManifestVerifier.cs','core/RuntimeLauncher.cs','HistoricalIcuCompatibility.cs') | ForEach-Object {Join-Path $prototype $_}
$output=Join-Path $PSScriptRoot 'HistoricalIcuTests.exe'
& $compiler /nologo /target:exe /platform:x64 /utf8output /main:DotaArchive.HistoricalIcuTests ('/out:'+$output) /r:System.Web.Extensions.dll @sources (Join-Path $PSScriptRoot 'HistoricalIcuTests.cs')
if($LASTEXITCODE -ne 0){throw 'Historical ICU tests failed to compile.'}
& $output $PSScriptRoot ([IO.Path]::GetFullPath($FixtureDirectory))
if($LASTEXITCODE -ne 0){throw 'Historical ICU tests failed.'}

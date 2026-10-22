param(
    [string]$SdkDirectory=(Join-Path (Split-Path $PSScriptRoot -Parent) 'toolchains/dotnet9/sdk-9.0.319'),
    [string]$LocalPackageFeed,
    [switch]$Restore,
    [switch]$DirectNetwork
)
$ErrorActionPreference='Stop'
$SdkDirectory=[IO.Path]::GetFullPath($SdkDirectory)
$dotnet=Join-Path $SdkDirectory 'dotnet.exe'
$buildRoot=Join-Path $PSScriptRoot 'build'
$output=Join-Path $PSScriptRoot 'artifacts/win-x64'
New-Item -ItemType Directory -Force -Path $buildRoot,$output | Out-Null
$env:DOTNET_ROOT=$SdkDirectory
$env:DOTNET_CLI_HOME=Join-Path $buildRoot 'cli-home'
$env:NUGET_PACKAGES=Join-Path $buildRoot 'nuget-packages'
$env:NUGET_HTTP_CACHE_PATH=Join-Path $buildRoot 'nuget-http-cache'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false'
$env:DOTNET_NOLOGO='1'
# Optional shell-local direct connection. Never change machine or user settings.
if($DirectNetwork){$env:HTTP_PROXY=$null;$env:HTTPS_PROXY=$null;$env:ALL_PROXY=$null}
$config=Join-Path $buildRoot 'nuget.config'
$feedXml=''
if($LocalPackageFeed){
    $LocalPackageFeed=[IO.Path]::GetFullPath($LocalPackageFeed)
    if(!(Test-Path -LiteralPath $LocalPackageFeed)){throw 'The optional local package feed does not exist.'}
    $feedXml='<add key="local" value="'+[Security.SecurityElement]::Escape($LocalPackageFeed)+'"/>'
}
[IO.File]::WriteAllText($config,('<configuration><packageSources><clear/>'+$feedXml+'<add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>'))
$project=Join-Path $PSScriptRoot 'DepotDownloader/DepotDownloader.csproj'
if($Restore){
    & $dotnet restore $project --runtime win-x64 --configfile $config --packages $env:NUGET_PACKAGES -p:SelfContained=true --verbosity minimal 2>&1 | Tee-Object -FilePath (Join-Path $buildRoot 'restore.log')
    if($LASTEXITCODE -ne 0){throw 'Historical engine NuGet restore failed.'}
}
& $dotnet publish $project --no-restore --configuration Release --runtime win-x64 --self-contained -p:PublishSingleFile=true -p:DebugType=embedded -p:Version=3.4.0-history.1 -p:InformationalVersion=3.4.0-history.1+c553ef4 --output $output 2>&1 | Tee-Object -FilePath (Join-Path $buildRoot 'publish.log')
if($LASTEXITCODE -ne 0){throw 'Historical engine publication failed.'}
$testProject=Join-Path $PSScriptRoot 'Tests/HistoricalDepotPolicyTests.csproj'
& $dotnet restore $testProject --configfile $config --packages $env:NUGET_PACKAGES --ignore-failed-sources --verbosity minimal
if($LASTEXITCODE -ne 0){throw 'Offline policy test project restore failed.'}
& $dotnet run --project $testProject --no-restore --configuration Release 2>&1 | Tee-Object -FilePath (Join-Path $buildRoot 'policy-tests.log')
if($LASTEXITCODE -ne 0){throw 'Offline policy checks failed.'}
$exe=Join-Path $output 'DepotDownloader.exe'
$files=@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'DepotDownloader') -File | Where-Object {$_.Extension -in @('.cs','.csproj')} | Sort-Object Name | ForEach-Object {@{Path=$_.Name;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
[ordered]@{Passed=$true;Version='3.4.0-history.1+c553ef4';UpstreamCommit='c553ef4d60c00a4f5fd16c9fe017f569001589ff';SdkVersion=(& $dotnet --version);Target='net9.0/win-x64/self-contained';EnginePath=$exe;EngineSha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant();OfflinePolicyChecks=18;SteamRequestsMade=$false;UserCredentialCacheRead=$false;Files=$files} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $buildRoot 'build-result.json') -Encoding UTF8
Get-Content -LiteralPath (Join-Path $buildRoot 'build-result.json')

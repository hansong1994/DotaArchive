param(
    [string]$OutputDirectory=(Join-Path $PSScriptRoot 'bin\data\patch-dates'),
    [string]$SourceDirectory=(Join-Path (Split-Path $PSScriptRoot -Parent) 'offline-patch-catalog')
)
$ErrorActionPreference='Stop'
$SourceDirectory=[IO.Path]::GetFullPath($SourceDirectory)
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if($OutputDirectory -match '[^\x00-\x7F]'){throw 'Patch date output paths must use ASCII characters.'}
$catalogPath=Join-Path $SourceDirectory 'patch-release-catalog.json'
$catalog=Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
if($catalog.SchemaVersion -ne 1 -or $catalog.AppId -ne '570' -or !$catalog.Patches -or $catalog.Coverage.Count -ne $catalog.Patches.Count){throw 'Invalid official patch date catalog.'}
$files=@()
foreach($record in $catalog.SourceFiles){
    if($record.File -notmatch '^sources/[A-Za-z0-9._-]+\.(json|html)$'){throw 'Unexpected patch date source path.'}
    $uri=[Uri]$record.Url
    if($uri.Scheme -ne 'https' -or $uri.Host -notin @('www.dota2.com','dota2.com','www.dota2.com.cn','api.steampowered.com','store.steampowered.com','steamcommunity.com')){throw 'Unexpected patch date source host.'}
    $path=Join-Path $SourceDirectory $record.File
    if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $record.Sha256){throw ('Patch date evidence hash mismatch: '+$record.File)}
    $files+=@{Path=$record.File;Url=$record.Url;Sha256=$record.Sha256;Bytes=(Get-Item -LiteralPath $path).Length;ContentKind=if($record.ContentKind){$record.ContentKind}else{'OfficialApiResponse'}}
}
if(!$files.Count -or @($files.Path|Select-Object -Unique).Count -ne $files.Count){throw 'Missing or duplicate patch date evidence.'}
New-Item -ItemType Directory -Path (Join-Path $OutputDirectory 'sources') -Force | Out-Null
Copy-Item -LiteralPath $catalogPath -Destination (Join-Path $OutputDirectory 'patch-release-catalog.json') -Force
foreach($record in $files){Copy-Item -LiteralPath (Join-Path $SourceDirectory $record.Path) -Destination (Join-Path $OutputDirectory $record.Path) -Force}
$generator=Join-Path $SourceDirectory 'Build-Catalog.ps1'
$provenance=[ordered]@{SchemaVersion=1;AppId='570';CatalogSha256=(Get-FileHash -LiteralPath $catalogPath -Algorithm SHA256).Hash.ToLowerInvariant();PatchCount=$catalog.Patches.Count;GeneratedAtUtc=$catalog.GeneratedAtUtc;SourceFiles=$files;GeneratorSource='PatchDateCatalog/Build-Catalog.ps1';GeneratorSha256=(Get-FileHash -LiteralPath $generator -Algorithm SHA256).Hash.ToLowerInvariant();ManualBuildMapping=$false;ExactReleaseTimeVerified=$false;CompleteVersionInventory=$false;CredentialsIncluded=$false}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'provenance.json'),($provenance|ConvertTo-Json -Depth 8),(New-Object Text.UTF8Encoding($false)))
Write-Output $OutputDirectory

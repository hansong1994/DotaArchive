param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$prototypeRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$workRoot=Split-Path $prototypeRoot -Parent
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$utf8=New-Object Text.UTF8Encoding($false)
function Hash-Text([string]$Value){
    $sha=[Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash($utf8.GetBytes($Value)))).Replace('-','').ToLowerInvariant() } finally {$sha.Dispose()}
}
$sources=New-Object 'Collections.Generic.List[object]'
function Save-Resource([string]$Kind,[string]$Url,[string]$Payload,[string]$CapturedAtUtc,[string]$SourcePath){
    $record=[ordered]@{SchemaVersion=1;Kind=$Kind;Url=$Url;CapturedAtUtc=$CapturedAtUtc;Payload=$Payload;PayloadSha256=(Hash-Text $Payload)}
    $name='resource-'+(Hash-Text $Url)+'.json'
    [IO.File]::WriteAllText((Join-Path $OutputDirectory $name),($record|ConvertTo-Json -Depth 10 -Compress),$utf8)
    $sources.Add([ordered]@{SourcePath=$SourcePath;SourceSha256=(Get-FileHash -LiteralPath $SourcePath -Algorithm SHA256).Hash.ToLowerInvariant();SourceUrl=$Url;OutputFile=$name;OutputSha256=(Get-FileHash -LiteralPath (Join-Path $OutputDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant()})
}
$official=[ordered]@{
    'official-patchnoteslist.json'='https://www.dota2.com/datafeed/patchnoteslist?language=english'
    'official-app570-news.json'='https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid=570&count=1000&maxlength=0&format=json&feeds=steam_community_announcements'
    'official-app570-steam-updates.json'='https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid=570&count=1000&maxlength=0&format=json&feeds=steam_updates'
}
foreach($item in $official.GetEnumerator()){
    $sourcePath=Join-Path (Join-Path $workRoot 'official-patch-evidence') $item.Key
    Save-Resource 'OfficialResponse' $item.Value ([IO.File]::ReadAllText($sourcePath)) ((Get-Item -LiteralPath $sourcePath).LastWriteTimeUtc.ToString('o')) $sourcePath
}
$snapshotPath=Join-Path $prototypeRoot 'fixtures/20261018-044232-246-app-570-patchnotes.json'
$snapshotText=[IO.File]::ReadAllText($snapshotPath)
$snapshot=$snapshotText|ConvertFrom-Json
if($snapshot.Url -ne 'https://steamdb.info/app/570/patchnotes/'){throw 'Unexpected source URL in the saved patch listing.'}
# Read the literal timestamp independently of ConvertFrom-Json date conversion.
# This keeps the same UTC instant in Windows PowerShell 5.1 and 7.
$capturedText=([regex]::Match($snapshotText,'"CapturedAtUtc"\s*:\s*"(?<timestamp>[^"]+)"')).Groups['timestamp'].Value.Replace('\/','/')
if($capturedText -match '^/Date\((-?\d+)\)/$'){$captured=[DateTimeOffset]::FromUnixTimeMilliseconds([long]$Matches[1]).ToString('o')}
else {$captured=[DateTimeOffset]::Parse($capturedText,[Globalization.CultureInfo]::InvariantCulture).ToUniversalTime().ToString('o')}
$payload=[ordered]@{Url=$snapshot.Url;Title=$snapshot.Title;Html=$snapshot.Html;Text=$snapshot.Text;CapturedAtUtc=$captured}|ConvertTo-Json -Depth 10 -Compress
Save-Resource 'BrowserSnapshot' $snapshot.Url $payload $captured $snapshotPath
[ordered]@{SchemaVersion=1;CreatedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');Purpose='Raw official metadata and browser evidence replay; no manual patch-to-build or depot index.';Sources=@($sources.ToArray())}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $OutputDirectory 'pack-provenance.json') -Encoding utf8
Write-Output $OutputDirectory

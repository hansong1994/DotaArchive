param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$archiveRoot=Join-Path (Split-Path $PSScriptRoot -Parent) 'dota-history-archive-2013-2015'
$completion=Get-Content -LiteralPath (Join-Path $archiveRoot 'completion-report.json') -Raw | ConvertFrom-Json
if($completion.AppId -ne '570' -or $completion.Branch -ne 'public' -or !$completion.RangeCovered -or !$completion.AllChainsReplayVerified){ throw 'Local archive completion evidence is not verified.' }
$verification=Join-Path $archiveRoot $completion.ChainVerificationReport
if((Get-FileHash -LiteralPath $verification -Algorithm SHA256).Hash -ine $completion.ChainVerificationReportSha256){ throw 'Local archive verification report changed.' }
$destination=Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'data\history'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$files=@()
foreach($property in $completion.CacheSha256.PSObject.Properties){
    if($property.Name -notin @('verified-public-history-v1.json','verified-public-anchors-v2.json')){ throw 'Unexpected archive input name.' }
    $source=Join-Path (Join-Path $archiveRoot 'cache') $property.Name
    if((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ine $property.Value){ throw ('Archive source changed: '+$property.Name) }
    $target=Join-Path $destination $property.Name
    Copy-Item -LiteralPath $source -Destination $target -Force
    if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ine $property.Value){ throw ('Archive package verification failed: '+$property.Name) }
    $files+=@{Name=$property.Name;Sha256=$property.Value;Bytes=(Get-Item -LiteralPath $target).Length}
}
@{
    SchemaVersion=1;AppId='570';Branch='public';Filter='root_depots';
    Origin='Verified continuous SteamDB baseline and history; no manually entered version table';
    AnchorIdentity=$completion.AnchorIdentity;TotalContinuousRecords=$completion.TotalContinuousRecords;
    RequestedRangeRecords=$completion.RequestedRangeRecords;RequestedLowerUtc=$completion.RequestedLowerUtc;
    UpperExclusiveUtc=$completion.UpperExclusiveUtc;OldestChangeId=$completion.BoundaryChangeId;
    OldestObservedAtUtc=$completion.BoundaryObservedAtUtc;
    TerminalEvidenceSource=$completion.TerminalEvidenceSource;
    AutomatedWebsiteEndMarkerObserved=$completion.AutomatedWebsiteEndMarkerObserved;
    ArchiveFiles=$files
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $destination 'archive-provenance.json') -Encoding UTF8
Write-Output ('Packaged verified continuous archive: '+$completion.TotalContinuousRecords+' changes')

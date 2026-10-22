param([string]$SourceDirectory = (Join-Path $PSScriptRoot '..\official-patch-evidence'))
$ErrorActionPreference = 'Stop'
$patchUrl = 'https://www.dota2.com/datafeed/patchnoteslist?language=english'
$newsBase = 'https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid=570&count=1000&maxlength=0&format=json&feeds='
$evidence = [Collections.Generic.List[object]]::new()
$ignored = [Collections.Generic.List[object]]::new()
$sources = [Collections.Generic.List[object]]::new()
$unresolved = [Collections.Generic.List[object]]::new()
$rawDirectory = Join-Path $PSScriptRoot 'sources'
New-Item -ItemType Directory -Path $rawDirectory -Force | Out-Null
if (!(Test-Path -LiteralPath (Join-Path $SourceDirectory 'official-patchnoteslist.json'))) { $SourceDirectory = $rawDirectory }
foreach ($file in @('official-patchnoteslist.json','official-app570-news.json','official-app570-steam-updates.json')) {
    $path = Join-Path $SourceDirectory $file
    if ([IO.Path]::GetFullPath($path) -ne [IO.Path]::GetFullPath((Join-Path $rawDirectory $file))) { Copy-Item -LiteralPath $path -Destination (Join-Path $rawDirectory $file) -Force }
    $url = if ($file -eq 'official-patchnoteslist.json') { $patchUrl } elseif ($file -eq 'official-app570-news.json') { $newsBase + 'steam_community_announcements' } else { $newsBase + 'steam_updates' }
    $sources.Add([ordered]@{File='sources/'+$file;Url=$url;Sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant();LocalEvidenceLastWriteAtUtc=(Get-Item -LiteralPath $path).LastWriteTimeUtc.ToString('o')})
}
foreach ($regionalSource in Get-ChildItem -LiteralPath $SourceDirectory -Filter 'official-cn-*.json') {
    $destination = Join-Path $rawDirectory $regionalSource.Name
    if ([IO.Path]::GetFullPath($regionalSource.FullName) -ne [IO.Path]::GetFullPath($destination)) { Copy-Item -LiteralPath $regionalSource.FullName -Destination $destination -Force }
}
$list = Get-Content -LiteralPath (Join-Path $SourceDirectory 'official-patchnoteslist.json') -Raw | ConvertFrom-Json
if (!$list.success) { throw 'Official patch list is not successful.' }
$pacific = [TimeZoneInfo]::FindSystemTimeZoneById('Pacific Standard Time')
foreach ($p in $list.patches) {
    if ($p.patch_number -notmatch '^[67]\.[0-9]{2}[a-z]?$') { throw 'Unsupported official patch identity.' }
    $at = [DateTimeOffset]::FromUnixTimeSeconds($p.patch_timestamp)
    $local = [TimeZoneInfo]::ConvertTime($at, $pacific)
    if ($local.TimeOfDay -ne [TimeSpan]::Zero) { throw 'Official patch date no longer follows the saved midnight convention; review rather than infer.' }
    $next = [TimeZoneInfo]::ConvertTimeToUtc([DateTime]::SpecifyKind($local.Date.AddDays(1), [DateTimeKind]::Unspecified), $pacific)
    $evidence.Add([ordered]@{Patch=$p.patch_number;EvidenceKind='OfficialPatchList';Date=$local.ToString('yyyy-MM-dd');Precision='date-only';SourceTimeZone='America/Los_Angeles';SourceTimeZoneNote='Catalog query convention: every captured epoch equals Pacific local midnight. Retain the raw epoch and next Pacific midnight for a date window; this is not a claim that Valve published the client then or explicitly documented this timezone.';AnnouncedAtUtc=$null;RawTimestampUtc=$at.ToString('yyyy-MM-ddTHH:mm:ssZ');DateBoundaryUtc=$at.ToString('yyyy-MM-ddTHH:mm:ssZ');DateEndUtc=$next.ToString('yyyy-MM-ddTHH:mm:ssZ');SourceUrl=$patchUrl;ArticleUrl='https://www.dota2.com/patches/'+$p.patch_number;Title=$p.patch_name;NewsId=$null;SourceFile='sources/official-patchnoteslist.json';PublicAvailability='official-patch-date';IdentityEvidence='patch_number field';Notes=@()})
}
$versionPattern = '(?<![0-9A-Za-z.])[67]\.[0-9]{2}[a-z]?(?![0-9A-Za-z.])'
foreach ($file in @('official-app570-news.json','official-app570-steam-updates.json')) {
    $doc = Get-Content -LiteralPath (Join-Path $SourceDirectory $file) -Raw | ConvertFrom-Json
    if ($doc.appnews.appid -ne 570 -or $doc.appnews.newsitems.Count -ge 1000) { throw 'Unexpected or potentially truncated news feed.' }
    foreach ($n in $doc.appnews.newsitems) {
        if ($n.appid -ne 570 -or $n.feedname -notin @('steam_community_announcements','steam_updates')) { throw 'Non-official app/feed.' }
        $text = [Net.WebUtility]::HtmlDecode(($n.contents -replace '<br\s*/?>',"`n" -replace '<[^>]*>',' ' -replace '\\r\\n',"`n" -replace '\[(?:/?[A-Za-z]+)(?:=[^\]]*)?\]',' '))
        $matches = @([regex]::Matches($n.title,$versionPattern) | ForEach-Object Value | Select-Object -Unique)
        $identity = 'announcement title'
        if ($n.feedname -eq 'steam_updates' -and $matches.Count -eq 0) {
            $matches = @([regex]::Matches($text,'(?im)^\s*([67]\.[0-9]{2}[a-z]?)(?:\s*(?::\s*|Gameplay\s+(?:Update|Changes)\s*:?)?(?:\r?\n|$))') | ForEach-Object {$_.Groups[1].Value.ToLowerInvariant()} | Select-Object -Unique)
            $identity = 'standalone patch heading in official update body'
        }
        # Named 7.03 article explicitly says that this release includes the 7.03 gameplay update.
        if ($n.title -eq 'The Bladeform Legacy Update' -and $text -match 'this release includes[\s\S]*the 7\.03 gameplay update') { $matches=@('7.03'); $identity='explicit included patch in release statement' }
        # The October 29 notes defer 6.85b until after Nanyang; the November 1 update is its actual numbered change list.
        if ($n.gid -eq '378660375683708734' -and $text -match '6\.85b:') { $matches=@('6.85b'); $identity='numbered changes in official public update notes' }
        if ($matches.Count -eq 0) { continue }
        $at = [DateTimeOffset]::FromUnixTimeSeconds($n.date)
        foreach ($patch in $matches) {
            $reason=$null
            if ($n.title -match '(?i)countdown|preview|upcoming|hotfix|bug.?fix|coming soon|retrospective|test client') { $reason='preview or follow-up title' }
            if ($text -match '(?i)will be released today|before it hits the main client|Once the update is released next week') { $reason='future or test-client announcement' }
            if ($reason) { $ignored.Add([ordered]@{Patch=$patch;NewsId=$n.gid;Title=$n.title;AnnouncedAtUtc=$at.ToString('yyyy-MM-ddTHH:mm:ssZ');Reason=$reason});continue }
            $notes=@('Precise time identifies publication of the official article, not the exact public client/build release.')
            if ($patch -eq '6.88c' -and $n.title -match 'August 19th 2016' -and $at.ToString('yyyy-MM-dd') -eq '2016-08-17') { $notes+= 'The article title says August 19th while the API publication timestamp says August 17th. Release date remains ambiguous.' }
            $article = $n.url -replace '^http:','https:'
            $availability = if ($n.feedname -eq 'steam_updates') {'official-update-notes'} else {'release-announcement'}
            $evidence.Add([ordered]@{Patch=$patch;EvidenceKind=if($n.feedname -eq 'steam_updates'){'OfficialSteamUpdate'}else{'OfficialAnnouncement'};Date=$at.ToString('yyyy-MM-dd');Precision='announcement-time';SourceTimeZone='UTC';SourceTimeZoneNote='Official API Unix timestamp of the article.';AnnouncedAtUtc=$at.ToString('yyyy-MM-ddTHH:mm:ssZ');RawTimestampUtc=$at.ToString('yyyy-MM-ddTHH:mm:ssZ');SourceUrl=$newsBase+$n.feedname;ArticleUrl=$article;Title=$n.title;NewsId=$n.gid;SourceFile='sources/'+$file;PublicAvailability=$availability;IdentityEvidence=$identity;Notes=$notes})
        }
    }
}
# Regional release-day observations are primary publisher evidence, not hand-written Build indexes.
foreach ($source in Get-ChildItem -LiteralPath $rawDirectory -Filter 'official-cn-*.json') {
    $r = Get-Content -LiteralPath $source.FullName -Raw | ConvertFrom-Json
    if ($r.Patch -notmatch '^[67]\.[0-9]{2}[a-z]?$' -or $r.SourceTimeZone -notin @('Asia/Shanghai','unspecified') -or $r.Precision -ne 'date-only' -or ([uri]$r.Url).Host -ne 'www.dota2.com.cn') { throw 'Invalid regional official source.' }
    $offset=if($r.SourceTimeZone -eq 'Asia/Shanghai'){'+08:00'}else{'+00:00'}
    $zoneNote=if($r.SourceTimeZone -eq 'Asia/Shanghai'){'Official Chinese publisher calendar day; window uses Beijing time. Not a claimed exact global release instant.'}else{'The official update title gives a calendar date but no timezone. UTC midnight-to-midnight is an explicit query convention, not a claim of source timezone or exact public release instant.'}
    $start = [DateTimeOffset]::ParseExact($r.Date + 'T00:00:00'+$offset, 'yyyy-MM-ddTHH:mm:sszzz', [Globalization.CultureInfo]::InvariantCulture).ToUniversalTime()
    $sources.Add([ordered]@{File='sources/'+$source.Name;Url=$r.Url;Sha256=(Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash.ToLowerInvariant();ObservedOnUtc=$r.ObservedOnUtc;ContentKind='PrimarySourceObservation'})
    if ($r.Accepted -eq $false) { $unresolved.Add([ordered]@{Patch=$r.Patch;Reason=$r.UnresolvedReason;Sources=@($r.Url)+@($r.RelatedSourceUrls);SourceFile='sources/'+$source.Name}); continue }
    $evidence.Add([ordered]@{Patch=$r.Patch;EvidenceKind='OfficialRegionalReleaseDate';Date=$r.Date;Precision='date-only';SourceTimeZone=$r.SourceTimeZone;SourceTimeZoneNote=$zoneNote;AnnouncedAtUtc=$null;RawTimestampUtc=$null;DateBoundaryUtc=$start.ToString('yyyy-MM-ddTHH:mm:ssZ');DateEndUtc=$start.AddDays(1).ToString('yyyy-MM-ddTHH:mm:ssZ');SourceUrl=$r.Url;ArticleUrl=$r.Url;RelatedSourceUrls=@($r.RelatedSourceUrls);Title=$r.Title;NewsId=$null;SourceFile='sources/'+$source.Name;PublicAvailability='official-regional-release-day';IdentityEvidence=@($r.Facts) -join ' ';Notes=@($r.Notes)})
}
$patches=@($evidence | ForEach-Object { [pscustomobject]$_ } | Group-Object Patch | ForEach-Object {
    $items=@($_.Group | Sort-Object RawTimestampUtc,NewsId)
    $officialDate=@($items | Where-Object {$_.EvidenceKind -eq 'OfficialPatchList'})
    $announcements=@($items | Where-Object {$_.Precision -eq 'announcement-time'})
    $regional=@($items | Where-Object {$_.EvidenceKind -eq 'OfficialRegionalReleaseDate'})
    $preferred=if($announcements.Count){$announcements[0]}elseif($officialDate.Count){$officialDate[0]}else{$regional[0]}
    $boundary=if($preferred.DateBoundaryUtc){$preferred.DateBoundaryUtc}elseif($officialDate.Count){$officialDate[0].RawTimestampUtc}else{$preferred.Date+'T00:00:00Z'}
    $end=if($preferred.DateEndUtc){$preferred.DateEndUtc}else{[DateTimeOffset]::Parse($boundary).AddDays(1).ToString('yyyy-MM-ddTHH:mm:ssZ')}
    [pscustomobject][ordered]@{Patch=$_.Name;ReleaseDate=$preferred.Date;Precision=$preferred.Precision;SourceTimeZone=$preferred.SourceTimeZone;AnnouncedAtUtc=$preferred.AnnouncedAtUtc;RawTimestampUtc=$preferred.RawTimestampUtc;DateBoundaryUtc=$boundary;DateEndUtc=$end;OfficialPatchListBoundaryUtc=if($officialDate.Count){$officialDate[0].RawTimestampUtc}else{$null};ReleaseTimeVerified=$false;TimeMeaning=if($preferred.Precision -eq 'date-only'){'official patch calendar or regional publication date; no exact release time'}else{'official update/announcement publication; not verified exact release time'};Sources=@($items | ForEach-Object {$_.SourceUrl;$_.ArticleUrl;$_.RelatedSourceUrls} | Where-Object {$_} | Select-Object -Unique);Evidence=$items;Ambiguities=@($items | ForEach-Object {$_.Notes} | Where-Object {$_ -match 'ambiguous'} | Select-Object -Unique)}
} | Sort-Object @{Expression={[int]($_.Patch.Split('.')[0])}},@{Expression={[int]([regex]::Match($_.Patch,'\.([0-9]{2})').Groups[1].Value)}},Patch)
$unresolved.Add([ordered]@{Patch='6.86';Reason='The named official announcement precedes public availability; a later regional tournament recap says December 16 without timezone. No sufficiently precise official public release date was verified.';Sources=@('https://www.dota2.com.cn/article/details/20160112/178951.html','https://www.dota2.com.cn/article/details/20151222/179521.html',$newsBase+'steam_community_announcements');SourceFile='sources/official-app570-news.json'})
$unresolved.Add([ordered]@{Patch='6.87';Reason='Official announcement explicitly directs users to the Test client and says the main client follows in a day or two; an exact public release date is not established.';Sources=@('https://www.dota2.com/687',$newsBase+'steam_community_announcements');SourceFile='sources/official-app570-news.json'})
$catalog=[ordered]@{SchemaVersion=1;AppId='570';GeneratedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');Purpose='Offline official patch date/announcement anchors. Contains no Build, Depot or Manifest mapping.';Coverage=[ordered]@{Count=$patches.Count;FirstPatch=$patches[0].Patch;LastPatch=$patches[-1].Patch;OfficialListCount=$list.patches.Count;CompleteVersionInventory=$false;LatestListReadVerifiedViaWebOn='2026-10-22';Limitations=@('Early official feeds do not identify every patch number or letter release. Missing versions must not be fabricated.','Announcement timestamps and date-only list values do not independently establish the first public Build.','6.88c has an unresolved official article title/API date discrepancy.','Main patch date anchors 6.84, 6.86 and 6.87 are unresolved. Other early releases may also be missing.');};Patches=$patches;UnresolvedPatches=$unresolved;SourceFiles=$sources;ExcludedEvidence=$ignored}
$catalog | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'patch-release-catalog.json') -Encoding utf8NoBOM
$report=[ordered]@{Count=$patches.Count;DateOnly=@($patches|Where-Object Precision -eq 'date-only').Count;AnnouncementTime=@($patches|Where-Object Precision -eq 'announcement-time').Count;Before708=@($patches|Where-Object {$_.Patch -match '^6\.' -or $_.Patch -match '^7\.0[0-7]'}|ForEach-Object { [ordered]@{Patch=$_.Patch;Date=$_.ReleaseDate;Precision=$_.Precision} });UnresolvedPatches=$unresolved;AmbiguousPatches=@($patches|Where-Object {$_.Ambiguities.Count}|Select-Object Patch,Ambiguities);ExcludedCount=$ignored.Count;SourceFileCount=$sources.Count;BuildMappingCount=0;SteamDbRequests=0}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'catalog-report.json') -Encoding utf8NoBOM
$report | ConvertTo-Json -Depth 8

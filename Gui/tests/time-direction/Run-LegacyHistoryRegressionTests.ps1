$ErrorActionPreference='Stop'
$prototype=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$dependency=Join-Path $prototype 'bin/HtmlAgilityPack.dll'
$sources=@('core/Models.cs','HistoryModels.cs','HistoryStateReducer.cs','SteamDbHistoryParser.cs','HistoryCacheStore.cs','HistoryAnchorStore.cs','DateFirstHistoryLocator.cs','OfficialPatchEvidence.cs','OnlineHistoryResolver.cs','HistoryBaselineMigration.cs','SyncCatalogStore.cs','LocalArchiveResolver.cs') | ForEach-Object {Join-Path $prototype $_}
$summary=@()
foreach($case in @(@{Name='DateFirstHistoryLocatorTests';Directory='date-first'},@{Name='HistoryResolverTests';Directory='history'})){
    $caseDirectory=Join-Path $prototype ('tests/'+$case.Directory)
    $output=Join-Path $caseDirectory ($case.Name+'.exe')
    Copy-Item -LiteralPath $dependency -Destination (Join-Path $caseDirectory 'HtmlAgilityPack.dll') -Force
    & $compiler /nologo /target:exe /platform:x64 /utf8output ('/main:DotaArchive.'+$case.Name) ('/out:'+$output) /r:System.Web.Extensions.dll ('/r:'+$dependency) @sources (Join-Path $prototype ($case.Name+'.cs'))
    if($LASTEXITCODE -ne 0){throw ($case.Name+' failed to compile.')}
    $lines=@(& $output)
    $lines | Write-Output
    if($LASTEXITCODE -ne 0){throw ($case.Name+' failed.')}
    $summary+=@{Name=$case.Name;Output=($lines -join "`n");Passed=$true}
}
@{Utc=[DateTime]::UtcNow.ToString('o');Suites=$summary;NetworkUsed=$false;DownloadsStarted=$false} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'legacy-results.json') -Encoding UTF8

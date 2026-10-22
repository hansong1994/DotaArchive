Dota Archive 1.0.8 corresponding source

The source archive contains Gui/ (the exact modified application and scripts),
DepotDownloader/ (the complete modified engine, based on upstream commit
c553ef4d60c00a4f5fd16c9fe017f569001589ff), Rebuild.ps1 and LICENSE.txt.
The engine is 3.4.0-history.1+c553ef4, not an unmodified SteamRE release.
Its changed files carry dated notices; HISTORICAL-COMPATIBILITY.txt describes
the changes. The source includes the optional -history policy and its tests.

To rebuild the GUI without downloading anything:
1. Extract the entire source zip to a short ASCII directory.
2. Keep the complete Dota Archive binary release in another directory.
3. Run Windows PowerShell 5.1 or PowerShell 7:
   powershell -ExecutionPolicy Bypass -File .\Rebuild.ps1 -ReleaseDirectory D:\DotaArchive -OutputDirectory D:\DotaArchiveRebuilt

Rebuild.ps1 compiles the GUI with Windows' .NET Framework C# compiler and the
included DLLs, then copies the public catalog and supplied download engine
from the specified release. No browser profile, Steam account or game is needed.
The output directory must not already exist. The compiler's executable hash
may differ because this legacy compiler embeds build-time metadata.

To rebuild the modified engine independently, use a portable .NET 9 SDK.
The release was built with SDK 9.0.319 and self-contained runtime 9.0.21.
For example, from the extracted source root:
  powershell -ExecutionPolicy Bypass -File .\DepotDownloader\Build-HistoricalEngine.ps1 -SdkDirectory D:\dotnet9 -Restore
The engine build restores the declared public NuGet dependencies, builds
artifacts/win-x64/DepotDownloader.exe and runs the offline policy tests.
It does not connect to Steam or install system components. Its cache and
diagnostics remain inside DepotDownloader/build. An optional -LocalPackageFeed
accepts a local dependency feed. -DirectNetwork affects only the build process
and is needed only when an unavailable proxy would prevent public downloads.
The original upstream README and project files remain available for reference.
SteamKit's corresponding source remains bundled so a compatible modified
library can be rebuilt and relinked into this GPLv2 engine.

Gui/package-release.ps1 is the maintainer's packaging entry point. It expects
the original verified archive, public patch evidence and an atomically committed
latest-sync catalog, and the official date evidence directory. The package
includes data/patch-dates with the source responses, dated primary-source
observations and SHA256 provenance.
PatchDateCatalog/Build-Catalog.ps1 rebuilds the date catalog from these public
source files, without SteamDB or any Build/Depot/Manifest mapping. Run it
in PowerShell 7 with -SourceDirectory D:\DotaArchive\data\patch-dates\sources.
Official dates or announcement times alone do not prove the first public Build.
Patch queries honor the selected Before or After direction. AnnouncedAtUtc is
the reference instant for announcement-time entries; DateBoundaryUtc is the
reference for date-only entries, using the start of that date as an explicit
query convention. An unspecified source timezone uses UTC as a convention,
not a verified release timezone. Both directions strictly exclude equality.
There is no 24-hour or date-window cutoff. Saved first-release proof cannot
override the selected direction. The reference and returned client may belong
to different named patches; the result is not a verified first-release claim.
All patch matching remains local, and missing or conflicting dates fail locally.
Gui/release/ReleaseDataTool.cs validates those inputs,
exports only public-content DOM projections and verifies the portable result.
The archive contains source and scripts, not private test captures or profiles.
Source-only regression scripts that rely on the maintainer's saved fixtures
may require those public fixtures separately; rebuilding the GUI does not.

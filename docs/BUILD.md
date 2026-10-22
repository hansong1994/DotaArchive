# 从源码构建

普通用户请直接下载 [Releases](https://github.com/hansong1994/DotaArchive/releases/latest) 中的 Windows 发布包。以下说明面向需要修改代码或自行编译的人。

## 离线重建图形界面

1. 克隆本仓库，或完整解压与发布版本对应的源码包，路径使用英文。
2. 下载并完整解压同一版本的 Windows 发布包，例如放到 `D:\DotaArchive`。其中包含重建所需的 DLL、下载引擎和公开历史数据库。
3. 在源码根目录运行 Windows PowerShell 5.1 或 PowerShell 7：

```powershell
powershell -ExecutionPolicy Bypass -File .\Rebuild.ps1 -ReleaseDirectory D:\DotaArchive -OutputDirectory D:\DotaArchiveRebuilt
```

输出目录必须尚不存在。脚本使用 Windows 自带的 .NET Framework C# 编译器，复制发布包中的依赖、引擎和数据库，不会登录 Steam、初始化浏览器或下载客户端。

编译完成后运行 `D:\DotaArchiveRebuilt\DotaArchive.exe`。旧式编译器可能嵌入构建时元数据，重新编译后的 EXE 哈希不要求与发行 EXE 相同。

## 下载引擎

`DepotDownloader/` 包含修改后的完整引擎源码，上游基于提交 `c553ef4d60c00a4f5fd16c9fe017f569001589ff`。修改说明见 [HISTORICAL-COMPATIBILITY.txt](../DepotDownloader/HISTORICAL-COMPATIBILITY.txt)。

引擎原发行构建使用 .NET SDK 9.0.319、自包含运行时 9.0.21。准备便携 SDK 后，在仓库根目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\DepotDownloader\Build-HistoricalEngine.ps1 -SdkDirectory D:\dotnet9 -Restore
```

此操作会还原项目声明的 NuGet 依赖并运行离线策略测试，不会连接 Steam。支持 `-LocalPackageFeed` 指定本地依赖源。仅在不可用代理影响构建下载时考虑 `-DirectNetwork`；该参数只影响构建进程。

SteamKit 对应源码保存在根目录的 `SteamKit-3.2.0-source.zip`。相关版本、来源和许可见 [第三方说明](../THIRD-PARTY-NOTICES.txt)。

## 数据与维护脚本

数据库放在 GitHub Release 附件里，不重复存入 Git 源码历史。`PatchDateCatalog/Build-Catalog.ps1` 可以用发布包附带的官方来源重新生成补丁日期目录：

```powershell
pwsh -File .\PatchDateCatalog\Build-Catalog.ps1 -SourceDirectory D:\DotaArchive\data\patch-dates\sources
```

`Gui/build.ps1`、`Gui/package-release.ps1`、历史归档准备脚本以及部分回归测试依赖维护工作区中的原始证据、依赖包和相邻目录。它们保留用于查看对应构建流程，**不是克隆仓库后直接运行的公共一键构建入口**。重建图形界面应使用根目录的 `Rebuild.ps1`。

`DepotDownloader/.github/` 是上游引擎自带的工作流，保留在嵌套目录中，不会作为本仓库根目录的 GitHub Actions 自动运行。

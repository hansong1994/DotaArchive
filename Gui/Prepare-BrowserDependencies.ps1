param([string]$OutputDirectory=(Join-Path $PSScriptRoot 'bin'))
$ErrorActionPreference='Stop'
$dependencyRoot=Join-Path $PSScriptRoot 'dependencies'
New-Item -ItemType Directory -Force -Path $dependencyRoot | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$packages=@(
 @{Id='microsoft.web.webview2';Version='1.0.4258.31';Sha256='56f7f4b8bf9aee4b8efefbbdd4f67d5f74ebd1b100ed0806da71bf76af481aa9';Folder='webview2-1.0.4258.31'},
 @{Id='htmlagilitypack';Version='1.13.1';Sha256='fc3f549e1c86eff1ee6e7ad872a7c0f5ac21cee093d0f606f8270b3e937afcce';Folder='htmlagilitypack-1.13.1'}
)
foreach($package in $packages){
 $name=$package.Id+'.'+$package.Version+'.nupkg'
 $archive=Join-Path $dependencyRoot $name
 $url='https://api.nuget.org/v3-flatcontainer/'+$package.Id+'/'+$package.Version+'/'+$name
 $package.Url=$url
 if(!(Test-Path -LiteralPath $archive)){Invoke-WebRequest -Uri $url -OutFile $archive}
 if((Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash -ine $package.Sha256){throw ('依赖包 SHA256 不匹配：'+$name)}
 $extracted=Join-Path $dependencyRoot $package.Folder
 if(!(Test-Path -LiteralPath $extracted)){[IO.Compression.ZipFile]::ExtractToDirectory($archive,$extracted)}
}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$files=@(
 'webview2-1.0.4258.31/lib/net462/Microsoft.Web.WebView2.Core.dll',
 'webview2-1.0.4258.31/lib/net462/Microsoft.Web.WebView2.WinForms.dll',
 'webview2-1.0.4258.31/runtimes/win-x64/native/WebView2Loader.dll',
 'htmlagilitypack-1.13.1/lib/Net45/HtmlAgilityPack.dll'
)
$expectedFiles=@{
 'Microsoft.Web.WebView2.Core.dll'='d60e6e94245078cb56e44ebd5f360dc04bf18e460136be93bfc4cfca43b98505'
 'Microsoft.Web.WebView2.WinForms.dll'='6a9e6856f23c463783c3afb0ca5d78f56324c86657162ccb16d5227b3233b32a'
 'WebView2Loader.dll'='3426dcc55fdfb8b5e7ac623cf33b4ccf283fbf68a0c5a65bddfdb4d749a8abee'
 'HtmlAgilityPack.dll'='f46f70c229bfac5219547aab428f51a702a64a02fef9e568cc9db62c51faae59'
}
$records=@()
foreach($file in $files){
 $source=Join-Path $dependencyRoot $file
 $target=Join-Path $OutputDirectory ([IO.Path]::GetFileName($file))
 if((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ine $expectedFiles[[IO.Path]::GetFileName($file)]){throw ('解压依赖文件 SHA256 不匹配：'+$file)}
 if(!(Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ine $expectedFiles[[IO.Path]::GetFileName($file)]){Copy-Item -LiteralPath $source -Destination $target -Force}
 $records+=@{Path=$file;OutputFile=[IO.Path]::GetFileName($file);Bytes=(Get-Item -LiteralPath $source).Length;Sha256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$licenseRoot=Join-Path $OutputDirectory 'licenses'
New-Item -ItemType Directory -Force -Path $licenseRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $dependencyRoot 'webview2-1.0.4258.31/LICENSE.txt') -Destination (Join-Path $licenseRoot 'WebView2-LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $dependencyRoot 'htmlagilitypack-1.13.1/HtmlAgilityPack.nuspec') -Destination (Join-Path $licenseRoot 'HtmlAgilityPack.nuspec') -Force
@{SchemaVersion=1;Packages=$packages;RuntimePolicy='Uses installed Evergreen WebView2 Runtime. No runtime installer or system changes.';Files=$records} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'dependency-provenance.json') -Encoding UTF8
Write-Output $OutputDirectory

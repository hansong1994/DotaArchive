param([switch]$Offline)
$ErrorActionPreference='Stop'
$prototype=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if($Offline){
 $offlineFiles=@{
  'Microsoft.Web.WebView2.Core.dll'='d60e6e94245078cb56e44ebd5f360dc04bf18e460136be93bfc4cfca43b98505'
  'Microsoft.Web.WebView2.WinForms.dll'='6a9e6856f23c463783c3afb0ca5d78f56324c86657162ccb16d5227b3233b32a'
 }
 foreach($name in $offlineFiles.Keys){
  $dependency=Join-Path $PSScriptRoot $name
  if(!(Test-Path -LiteralPath $dependency) -or (Get-FileHash -LiteralPath $dependency -Algorithm SHA256).Hash -ine $offlineFiles[$name]){throw ('Offline dependency missing or hash mismatch: '+$name)}
 }
}else{
 & (Join-Path $prototype 'Prepare-BrowserDependencies.ps1') -OutputDirectory $PSScriptRoot | Out-Null
}
$sources=@('BrowserSession.cs','BrowserProbe.cs','HistoryModels.cs','HistoryStateReducer.cs','core/Models.cs') | ForEach-Object {Join-Path $prototype $_}
$output=Join-Path $PSScriptRoot 'BrowserPolicyTests.exe'
& $compiler /nologo /target:exe /platform:x64 /utf8output /main:BrowserPolicyTests /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll ('/r:'+(Join-Path $PSScriptRoot 'Microsoft.Web.WebView2.Core.dll')) ('/r:'+(Join-Path $PSScriptRoot 'Microsoft.Web.WebView2.WinForms.dll')) ('/out:'+$output) (Join-Path $PSScriptRoot 'BrowserPolicyTests.cs') @sources
if($LASTEXITCODE -ne 0){throw 'Browser policy test compilation failed'}
& $output | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'browser-policy-results.json') -Encoding UTF8
if($LASTEXITCODE -ne 0){throw 'Browser policy tests failed'}
Get-Content -LiteralPath (Join-Path $PSScriptRoot 'browser-policy-results.json')
& node (Join-Path $PSScriptRoot 'BrowserDomPolicyTests.js') | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'browser-dom-policy-results.json') -Encoding UTF8
if($LASTEXITCODE -ne 0){throw 'Browser DOM policy tests failed'}
Get-Content -LiteralPath (Join-Path $PSScriptRoot 'browser-dom-policy-results.json')

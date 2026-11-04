#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ConfigPath,
    [string]$ProjectRoot,
    [string]$BuildRoot,
    [string]$SecretsFile,
    [string]$AccountId=$env:CLOUDFLARE_ACCOUNT_ID,
    [string]$ApiToken=$env:CLOUDFLARE_API_TOKEN,
    [switch]$DryRun
)
$ErrorActionPreference='Stop'
$ConfigPath=(Resolve-Path -LiteralPath $ConfigPath).Path
$configRoot=Split-Path $ConfigPath
if(!$ProjectRoot){$ProjectRoot=$configRoot}
$ProjectRoot=(Resolve-Path -LiteralPath $ProjectRoot).Path
$raw=Get-Content -LiteralPath $ConfigPath -Raw
if($raw -notmatch '(?m)^\s*["'']?main["'']?\s*[:=]\s*["'']([^"'']+)["'']'){throw 'main が見つかりません。'}
$main=Join-Path $configRoot $Matches[1]
if(!$BuildRoot){$BuildRoot=Join-Path $configRoot '.artifacts/native-worker'}
$BuildRoot=[IO.Path]::GetFullPath($BuildRoot)
for($ancestor=$BuildRoot;$ancestor;$ancestor=Split-Path $ancestor -Parent){
    if(Test-Path -LiteralPath $ancestor){if((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw "ビルド先にリンクがあります: $ancestor"}}
}
$entry=Join-Path $BuildRoot 'entry.mjs'
$buildArgs=@('--main',$main,'--out',$entry,'--root',$ProjectRoot,'--project',$ProjectRoot)
if($raw -match '(?m)^\s*["'']?minify["'']?\s*[:=]\s*true'){$buildArgs+='--minify'}
& node (Join-Path $PSScriptRoot 'Build-CloudflareWorker.mjs') @buildArgs
if($LASTEXITCODE -ne 0){throw 'Worker の製品ビルドに失敗しました。'}
& (Join-Path $PSScriptRoot 'Publish-CloudflareWorker.ps1') -ConfigPath $ConfigPath -Main $entry -ModuleRoot $BuildRoot -ModuleManifestPath (Join-Path $BuildRoot 'modules.json') -SecretsFile $SecretsFile -AccountId $AccountId -ApiToken $ApiToken -DryRun:$DryRun

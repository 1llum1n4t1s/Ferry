#requires -Version 7.0
<#
既にビルド済みのWorkerを、同梱C# coreとPowerShell内蔵.NETで配信する。
Node/Wrangler/.NET SDKは不要。引数と戻り値は従来の入口を維持する。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ConfigPath,
    [string]$Main,
    [string]$ModuleRoot,
    [string]$ModuleManifestPath,
    [string]$SecretsFile,
    [string]$AccountId = $env:CLOUDFLARE_ACCOUNT_ID,
    [string]$ApiToken = $env:CLOUDFLARE_API_TOKEN,
    [string]$ApiBase = 'https://api.cloudflare.com/client/v4',
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$corePath = Join-Path $PSScriptRoot 'CloudflareWorkerPublisher.cs'
$source = [IO.File]::ReadAllText($corePath)
$sha = [Security.Cryptography.SHA256]::Create()
try { $hash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($source))).Replace('-', '') }
finally { $sha.Dispose() }
# 同一プロセスの別repo/別版もソースhashで型を分離し、古い型を再利用しない。
$typeName = 'CloudflarePublisher_' + $hash + '.Publisher'
$core = $typeName -as [type]
if (-not $core) {
    $compiledSource = $source.Replace('__NAMESPACE__', ('CloudflarePublisher_' + $hash))
    $references = @((Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll').FullName)
    Add-Type -TypeDefinition $compiledSource -ReferencedAssemblies $references -ErrorAction Stop
    $core = $typeName -as [type]
}
$options = @{ConfigPath=$ConfigPath;Main=$Main;ModuleRoot=$ModuleRoot;ModuleManifestPath=$ModuleManifestPath;SecretsFile=$SecretsFile;AccountId=$AccountId;ApiToken=$ApiToken;ApiBase=$ApiBase;DryRun=[bool]$DryRun}
# Set-Locationはprocess cwdを変えない。明示入力だけPSの現在位置で解決し、
# 未指定Mainはconfig、未指定ModuleRootはMain所在基準の既定処理をC#側に残す。
foreach ($key in @('ConfigPath','Main','ModuleRoot','ModuleManifestPath','SecretsFile')) {
    if ($options[$key]) {
        $resolved = Resolve-Path -LiteralPath $options[$key] -ErrorAction Stop
        if ($resolved.Provider.Name -ne 'FileSystem') { throw "$key はFileSystemのパスを指定してください。" }
        $options[$key] = $resolved.ProviderPath
    }
}
try { $result = $core::Run(($options | ConvertTo-Json -Compress -Depth 100)) }
catch { throw [InvalidOperationException]::new($_.Exception.GetBaseException().Message) }
[pscustomobject]($result | ConvertFrom-Json -AsHashtable)

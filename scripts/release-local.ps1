# release-local.ps1 — 署名付き Velopack リリース (Windows チャンネル専用)
#
# 署名は Sigilune (Certum SimplySign のクラウド鍵を signtool /dlib で使う CLI) で行う。
# 通常のリリースは .github/workflows/release.yml (release/** push) の windows-release ジョブが
# macOS / Linux の配信後に本スクリプトを実行する。ローカル実行も同じ手順で動き、検証や再開に使う。
#
# ⚠️ Ferry はクロスプラットフォーム配信 (win-x64 / win-arm64 / osx-arm64 / linux-x64 / linux-arm64)。
#    本スクリプトが扱うのは Authenticode 署名対象の Windows 2 チャンネルのみ。
#    macOS / Linux チャンネルは同じ release.yml の r2-upload ジョブが配信する。
#    Bridge ページは CF 単独完結 (Step 6) で relay Worker の Static Assets 配信へ移行済み (deploy-relay.yml)。
#    クリーンアップは「Windows 以外のチャンネルの manifest を R2 から取得して保護」するため、
#    他チャンネルの nupkg を誤削除しない。
#
# 前提:
#   - Sigilune を使えること (scripts/Sigilune.Common.ps1 参照)。
#     ローカルは `sigilune setup` で Certum アカウントを登録済み、CI は SIGILUNE_EMAIL / SIGILUNE_TOTP_URI を渡す
#   - Directory.Build.props の <Version> がリリースしたいバージョンになっていること (/vava 済み)
#   - 環境変数 CLOUDFLARE_API_TOKEN (CI)、なければ D:\Dev\Secret\secrets.json の cloudflare.api_token (ローカル)
#
# 使い方:
#   pwsh scripts/release-local.ps1                # フルリリース (build + sign + upload + cleanup)
#   pwsh scripts/release-local.ps1 -SkipUpload    # ビルド + 署名のみ (アップロードしない動作確認用)
#   pwsh scripts/release-local.ps1 -UploadOnly    # 同版の検証済み署名パッケージを再ビルドせず公開
#   pwsh scripts/release-local.ps1 -Runtimes win-x64   # 対象 RID を絞る (テスト用)

[CmdletBinding()]
param(
    [switch]$SkipUpload,
    [switch]$UploadOnly,
    [string[]]$Runtimes = @('win-x64', 'win-arm64')
)




$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'R2Distribution.Common.ps1')
if ($SkipUpload -and $UploadOnly) { throw 'SkipUpload と UploadOnly は同時に指定できません' }

# ---- 定数 ----
# Velopack (vpk) は常に最新安定版を使う (ゆろ君ルール): NuGet から実行時に最新を解決して pin する
$VpkVersion = (Invoke-RestMethod 'https://api.nuget.org/v3-flatcontainer/vpk/index.json' -TimeoutSec 30).versions |
    Where-Object { $_ -notmatch '-' } | Select-Object -Last 1
if (-not $VpkVersion) { throw 'vpk の最新安定版バージョンの取得に失敗しました (NuGet API)' }
Write-Host "vpk 最新安定版: $VpkVersion"
$Bucket = 'ferry-updates'
$BaseUrl = 'https://ferry.kagayoi.com'
$AccountId = '10901bfadbf1005164774a7350082985'
$SecretsPath = 'D:\Dev\Secret\secrets.json'
# 署名者の CN。証明書の年次更新で thumbprint が変わっても同じ値で照合できる。
$CertSubjectName = 'Open Source Developer Yuichiro Shinozaki'

# Ferry はチャンネル名 = RID (Lhamiel の 'win' とは異なり win-x64 もフル表記)
$RuntimeMatrix = @{
    'win-x64'   = @{ Channel = 'win-x64' }
    'win-arm64' = @{ Channel = 'win-arm64' }
}
# R2 上に存在する全チャンネル (cleanup で Windows 以外の manifest を保護するために使う)
$AllChannels = @('win-x64', 'win-arm64', 'osx-arm64', 'linux-x64', 'linux-arm64')

$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

function Invoke-Native {
    param([string]$Description, [scriptblock]$Block)
    & $Block
    if ($LASTEXITCODE -ne 0) { throw "$Description が失敗しました (exit $LASTEXITCODE)" }
}

# 書込み先は全親階層を確認し、既存のリンクをたどらない。
function Assert-LocalPath([string]$Path, [string]$Root) {
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw "絶対パスではありません: $Path" }
    $full = [IO.Path]::GetFullPath($Path)
    $boundary = [IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    if ($full -ne $boundary -and -not $full.StartsWith($boundary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "作業範囲外です: $full"
    }
    for ($ancestor = $full; $ancestor; $ancestor = Split-Path $ancestor -Parent) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "リンクを含むパスです: $ancestor"
        }
    }
}

function Expand-ReleaseArchive([string]$Archive, [string]$Destination) {
    Assert-LocalPath $Destination $WorkDir
    if (Test-Path -LiteralPath $Destination) { throw "検証先が既に存在します: $Destination" }
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        # 全エントリを検証してから展開する。ZIP内の絶対パス・親参照・ADS・symlinkを拒否。
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\','/')
            if (-not $name -or $name.StartsWith('/') -or $name.Contains(':') -or $name -match '(^|/)\.\.?(\x2f|$)' -or
                (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw "危険なZIPエントリ: $name" }
            foreach ($part in $name.TrimEnd('/') -split '/') {
                if (-not $part -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
                    $part -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { throw "不正なZIPパス要素: $name" }
            }
            $path = [IO.Path]::GetFullPath((Join-Path $Destination $name))
            Assert-LocalPath $path $Destination
            if (-not $names.Add($path)) { throw "重複したZIPエントリ: $name" }
        }
        [IO.Directory]::CreateDirectory($Destination) | Out-Null
        foreach ($entry in $zip.Entries) {
            $path = [IO.Path]::GetFullPath((Join-Path $Destination $entry.FullName.Replace('\','/')))
            Assert-LocalPath $path $Destination
            if ($entry.FullName.EndsWith('/') -or $entry.FullName.EndsWith('\')) {
                [IO.Directory]::CreateDirectory($path) | Out-Null
            } else {
                [IO.Directory]::CreateDirectory((Split-Path $path -Parent)) | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $false)
            }
        }
    } finally { $zip.Dispose() }
}

function Assert-ReleaseExe([string]$Path, [int[]]$Machines, [switch]$CheckVersion) {
    Assert-LocalPath $Path $WorkDir
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw "MZヘッダがありません: $Path" }
        $stream.Position = 0x3C
        $pe = $reader.ReadInt32()
        if ($pe -lt 64 -or $pe -gt $stream.Length - 6) { throw "不正なPE位置: $Path" }
        $stream.Position = $pe
        if ($reader.ReadUInt32() -ne 0x4550 -or $Machines -notcontains $reader.ReadUInt16()) { throw "PEマシンが一致しません: $Path" }
    } finally { $reader.Dispose() }
    if ($CheckVersion) {
        $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductVersion
        if (($productVersion -split '\+', 2)[0] -ne $version) { throw "ProductVersionが一致しません: $Path ($productVersion)" }
    }
    $sig = Get-AuthenticodeSignature -LiteralPath $Path
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notlike "CN=$CertSubjectName,*") {
        throw "署名検証失敗: $Path ($($sig.Status))"
    }
}

function Assert-PackageMetadata([string]$Path, [hashtable]$Expected) {
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    try { $xml = [Xml.XmlDocument]::new(); $xml.Load($reader) } finally { $reader.Dispose() }
    foreach ($key in $Expected.Keys) {
        $nodes = $xml.SelectNodes("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='$key']")
        if ($nodes.Count -ne 1 -or $nodes[0].InnerText -cne $Expected[$key]) { throw "パッケージmetadata不一致: $Path ($key)" }
    }
}

function Assert-PublishedPayload([string]$Runtime, [string]$PackageAppDir, [string]$PortableAppDir) {
    $publishDir = Join-Path $WorkDir "publish-$Runtime"
    Assert-LocalPath $publishDir $WorkDir
    # UploadOnlyも同版のpublish出力を残して照合する。署名対象の主exeは別途検証する。
    $publishedExe = Join-Path $publishDir 'Ferry.exe'
    Assert-LocalPath $publishedExe $publishDir
    $publishedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($publishedExe).ProductVersion
    if (($publishedVersion -split '\+', 2)[0] -ne $version) { throw "publishの版が一致しません: $Runtime" }
    foreach ($file in Get-ChildItem -LiteralPath $publishDir -Recurse -File -Force) {
        Assert-LocalPath $file.FullName $publishDir
        $relative = [IO.Path]::GetRelativePath($publishDir, $file.FullName)
        # vpkの正規除外であるデバッグシンボルだけを除く。DLLやdat等の実行時資源は全て必須。
        if ($relative -eq 'Ferry.exe' -or $relative -match '(?i)(?:\.pdb|\.dbg)$|(?:^|[\\/])[^\\/]+\.dSYM(?:[\\/]|$)') { continue }
        $expectedHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        foreach ($appDir in @($PackageAppDir, $PortableAppDir)) {
            $payloadPath = Join-Path $appDir $relative
            Assert-LocalPath $payloadPath $appDir
            if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf) -or
                (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash -ne $expectedHash) {
                throw "publish由来の必須ファイルが欠落または不一致です: $Runtime/$relative ($appDir)"
            }
        }
    }
}

function Assert-ReleaseArtifacts {
    Assert-LocalPath $ArtifactsDir $WorkDir
    $actualRids = @(Get-ChildItem -LiteralPath $ArtifactsDir -Filter 'releases.*.json' -File | ForEach-Object { $_.Name -replace '^releases\.|\.json$','' } | Sort-Object)
    if (($actualRids -join ',') -ne (($Runtimes | Sort-Object) -join ',')) { throw 'manifestのRID集合が一致しません' }
    $script:VerificationDir = Join-Path $WorkDir ('verification-' + [Guid]::NewGuid().ToString('N'))
    Assert-LocalPath $VerificationDir $WorkDir
    [IO.Directory]::CreateDirectory($VerificationDir) | Out-Null
    $expectedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($runtime in $Runtimes) {
        $packageName = "Ferry-$version-$runtime-full.nupkg"
        $setupName = "Ferry-$runtime-Setup.exe"
        $portableName = "Ferry-$runtime-Portable.zip"
        foreach ($name in @($packageName, $setupName, $portableName, "releases.$runtime.json", "assets.$runtime.json", "RELEASES-$runtime")) { $expectedFiles.Add($name) | Out-Null }
        $manifest = Get-Content -LiteralPath (Join-Path $ArtifactsDir "releases.$runtime.json") -Raw | ConvertFrom-Json
        if (@($manifest.Assets).Count -ne 1) { throw "Fullパッケージが一意ではありません: $runtime" }
        $asset = $manifest.Assets[0]
        if ($asset.PackageId -cne 'Ferry' -or $asset.Version -cne $version -or $asset.Type -cne 'Full' -or $asset.FileName -cne $packageName -or [IO.Path]::GetFileName($asset.FileName) -cne $asset.FileName) { throw "manifest identity不一致: $runtime" }
        $package = Join-Path $ArtifactsDir $packageName
        if ($asset.Size -ne (Get-Item -LiteralPath $package).Length -or $asset.SHA256 -notmatch '^[a-fA-F0-9]{64}$' -or $asset.SHA256 -ne (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -or $asset.SHA1 -ne (Get-FileHash -LiteralPath $package -Algorithm SHA1).Hash) { throw "manifest hash/size不一致: $runtime" }
        $assets = @(Get-Content -LiteralPath (Join-Path $ArtifactsDir "assets.$runtime.json") -Raw | ConvertFrom-Json)
        foreach ($pair in @(@{ Type='Full'; Name=$packageName }, @{ Type='Installer'; Name=$setupName }, @{ Type='Portable'; Name=$portableName })) {
            if (@($assets | Where-Object { $_.Type -ceq $pair.Type -and $_.RelativeFileName -ceq $pair.Name }).Count -ne 1) { throw "assets参照不一致: $runtime" }
        }
        if ($assets.Count -ne 3) { throw "予期しないassets: $runtime" }
        $releases = (Get-Content -LiteralPath (Join-Path $ArtifactsDir "RELEASES-$runtime") -Raw).Trim() -split '\s+'
        if ($releases.Count -ne 3 -or $releases[0] -ne $asset.SHA1 -or $releases[1] -cne $packageName -or $releases[2] -ne [string]$asset.Size) { throw "RELEASES参照不一致: $runtime" }
        $unpacked = Join-Path $VerificationDir $runtime
        Expand-ReleaseArchive $package $unpacked
        $arch = if ($runtime -eq 'win-arm64') { 'arm64' } else { 'x64' }
        $machine = if ($runtime -eq 'win-arm64') { 0xAA64 } else { 0x8664 }
        $identity = @{ id='Ferry'; version=$version; channel=$runtime; mainExe='Ferry.exe'; os='win' }
        Assert-PackageMetadata (Join-Path $unpacked 'Ferry.nuspec') ($identity + @{ rid=$runtime; machineArchitecture=$arch })
        Assert-PackageMetadata (Join-Path $unpacked 'lib/app/sq.version') $identity
        $app = Join-Path $unpacked 'lib/app/Ferry.exe'
        Assert-ReleaseExe $app @($machine) -CheckVersion
        # SetupはVelopackのx86 bootstrapを含みうるため、appのRID一致を要求しない。
        Assert-ReleaseExe (Join-Path $ArtifactsDir $setupName) @(0x14C,0x8664,0xAA64) -CheckVersion
        $portable = Join-Path $VerificationDir "portable-$runtime"
        Expand-ReleaseArchive (Join-Path $ArtifactsDir $portableName) $portable
        Assert-PackageMetadata (Join-Path $portable 'current/sq.version') $identity
        Assert-ReleaseExe (Join-Path $portable 'current/Ferry.exe') @($machine) -CheckVersion
        Assert-ReleaseExe (Join-Path $portable 'Ferry.exe') @(0x14C,0x8664,0xAA64)
        if ((Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $portable 'current/Ferry.exe') -Algorithm SHA256).Hash) { throw "portableとnupkgの実appが異なります: $runtime" }
        Assert-PublishedPayload $runtime (Join-Path $unpacked 'lib/app') (Join-Path $portable 'current')
    }
    $files = @(Get-ChildItem -LiteralPath $ArtifactsDir -Force)
    if ($files.Count -ne $expectedFiles.Count -or @($files | Where-Object { $_.PSIsContainer -or -not $expectedFiles.Contains($_.Name) }).Count) { throw '未検証の成果物がartifactsに含まれています' }
    $script:VerifiedHashes = @{}
    foreach ($file in $files) { $script:VerifiedHashes[$file.Name] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
    Write-Host "✅ 全成果物の版・構成・PE・署名・manifestを確認: $ArtifactsDir"
}

function Assert-ImmutableRemotePackages {
    # R2自身の一覧を使い、CDNのキャッシュ済み404で既存packageを見落とさない。
    $headers = @{ Authorization = "Bearer $($env:CLOUDFLARE_API_TOKEN)" }
    $api = "https://api.cloudflare.com/client/v4/accounts/$AccountId/r2/buckets/$Bucket/objects?per_page=1000"
    $keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $cursor = ''
    do {
        $uri = $api + $(if ($cursor) { '&cursor=' + [uri]::EscapeDataString($cursor) })
        $response = Invoke-RestMethod -Uri $uri -Headers $headers -TimeoutSec 30
        if (-not $response.success) { throw '既存R2オブジェクトを確認できません' }
        foreach ($obj in $response.result) { $keys.Add($obj.key) | Out-Null }
        $info = $response.PSObject.Properties['result_info']
        $cursor = ''
        if ($info -and $info.Value) {
            $truncated = $info.Value.PSObject.Properties['is_truncated']
            if ($truncated -and $truncated.Value) {
                $next = $info.Value.PSObject.Properties['cursor']
                if (-not $next -or -not $next.Value) { throw 'R2一覧のページ継続情報がありません' }
                $cursor = [string]$next.Value
            }
        }
    } while ($cursor)
    foreach ($package in Get-ChildItem -LiteralPath $ArtifactsDir -Filter '*.nupkg' -File) {
        if (-not $keys.Contains($package.Name)) { continue }
        $remotePath = Join-Path $VerificationDir "remote-$($package.Name)"
        Assert-LocalPath $remotePath $WorkDir
        $objectUri = "https://api.cloudflare.com/client/v4/accounts/$AccountId/r2/buckets/$Bucket/objects/$([uri]::EscapeDataString($package.Name))"
        Invoke-WebRequest -Uri $objectUri -Headers $headers -OutFile $remotePath -TimeoutSec 300 -MaximumRedirection 0
        if ((Get-FileHash -LiteralPath $remotePath -Algorithm SHA256).Hash -ne $VerifiedHashes[$package.Name]) {
            throw "同じversionの公開packageが異なります。上書きを停止します: $($package.Name)"
        }
    }
}

# ---- 0. プリフライト ----
Write-Host '== プリフライト ==' -ForegroundColor Cyan

# Git Bash (MSYS) 経由で起動すると括弧入り環境変数が落ちて、Native AOT の
# リンク段 (Microsoft.NETCore.Native.targets) の vswhere.exe 解決が壊れるため補完する
if (-not ${env:ProgramFiles(x86)}) { ${env:ProgramFiles(x86)} = 'C:\Program Files (x86)' }

# VS 2026 の vcvarsall は PATH 上の vswhere.exe を呼ぶ (GitHub ランナーは PATH 済み)。
# ローカルでは VS Installer ディレクトリが PATH に無いので AOT リンクが落ちる → 追加
$vsInstallerDir = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ($env:PATH -notlike "*$vsInstallerDir*") { $env:PATH = "$env:PATH;$vsInstallerDir" }

# vpk (dotnet tool) は古い .NET ランタイム要求のことがある → 最新メジャーにロールフォワード
$env:DOTNET_ROLL_FORWARD = 'Major'

# XPath で取得 (member enumeration は Version を持たない PropertyGroup 混在時に StrictMode で throw する)
$versionNode = ([xml](Get-Content 'Directory.Build.props' -Raw)).SelectSingleNode('/Project/PropertyGroup/Version')
$version = if ($versionNode) { $versionNode.InnerText.Trim() } else { $null }
if (-not $version) { throw 'Directory.Build.props から <Version> を取得できませんでした' }
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw '作業ディレクトリに使えないバージョンです' }
$WorkDir = Join-Path $RepoRoot "local-release/$version"
$ArtifactsDir = Join-Path $WorkDir 'artifacts'
Assert-LocalPath $WorkDir $RepoRoot
if ($Runtimes.Count -eq 0 -or @($Runtimes | Sort-Object -Unique).Count -ne $Runtimes.Count) { throw 'runtime集合が空または重複しています' }
foreach ($runtime in $Runtimes) { if (-not $RuntimeMatrix.ContainsKey($runtime)) { throw "未知のruntime: $runtime" } }
if ((Test-Path -LiteralPath $WorkDir) -and @(Get-ChildItem -LiteralPath $WorkDir -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw '作業ディレクトリにリンクが含まれています' }
Write-Host "バージョン: $version"

# 署名の準備 (Sigilune の場所とクラウドへのログインを、ビルド前に確認して早く落とす)。
# 検証済みパッケージを再送するだけの -UploadOnly では署名しないためログインしない。
. (Join-Path $PSScriptRoot 'Sigilune.Common.ps1')
if (-not $UploadOnly) {
    $Sigilune = Resolve-SigiluneExe
    Connect-Sigilune -SigiluneExe $Sigilune
    # Velopack はファイルごとに別プロセスで署名テンプレートを実行する。Sigilune はログインを共有し、
    # 署名後に有効性・署名者・タイムスタンプを検証して失敗なら非0で終わる。
    $SignTemplate = "`"$Sigilune`" sign {{file}}"
}

# vpk を固定バージョンで用意
$vpkInstalled = (dotnet tool list --global | Select-String -SimpleMatch 'vpk') -match [regex]::Escape($VpkVersion)
if (-not $vpkInstalled) {
    Write-Host "vpk $VpkVersion をインストールします..."
    dotnet tool uninstall --global vpk 2>$null | Out-Null
    Invoke-Native 'vpk のインストール' { dotnet tool install --global vpk --version $VpkVersion }
}

# Cloudflare トークン (アップロード時のみ必要)
if (-not $SkipUpload) {
    # CI では Environment "release" の Secret が環境変数で渡る。ローカルは secrets.json から必要なキーだけ読む。
    if (-not $env:CLOUDFLARE_API_TOKEN) {
        $secrets = Get-Content $SecretsPath -Raw | ConvertFrom-Json
        if (-not $secrets.cloudflare.api_token) { throw "secrets.json に cloudflare.api_token が見つかりません" }
        $env:CLOUDFLARE_API_TOKEN = $secrets.cloudflare.api_token
    }
    $env:CLOUDFLARE_ACCOUNT_ID = $AccountId
}

if (-not $UploadOnly) {
if ((Test-Path -LiteralPath $WorkDir) -and @(Get-ChildItem -LiteralPath $WorkDir -Force).Count -gt 0) {
    throw "作業ディレクトリが空ではありません: $WorkDir。再送は -UploadOnly を使用し、再ビルドはPrunletで事前清掃してください。"
}
New-Item -ItemType Directory -Path $ArtifactsDir -Force | Out-Null

# ---- 1. ビルド + 署名付きパッケージング (RID ごと) ----
$originalBuildEnvironment = @{}
foreach ($name in @('PATH', 'INCLUDE', 'LIB', 'LIBPATH')) {
    $originalBuildEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
foreach ($runtime in $Runtimes) {
    # 前のRIDのVC環境を引き継がず、プリフライト後の同じ環境から初期化する。
    foreach ($name in $originalBuildEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, ($originalBuildEnvironment[$name] ?? [NullString]::Value), 'Process')
    }
    $config = $RuntimeMatrix[$runtime]
    if (-not $config) { throw "未知の runtime: $runtime (本スクリプトは Windows チャンネル専用)" }
    $publishDir = Join-Path $WorkDir "publish-$runtime"

    # VS 2026 のインストール構成によって旧 component ID の自動検出が失敗するため、
    # 対象アーキテクチャの公式開発環境を明示的に読み込む。
    $vsPath = & (Join-Path $vsInstallerDir 'vswhere.exe') -latest -prerelease -products '*' -property installationPath
    $vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvarsall.bat'
    if (-not (Test-Path -LiteralPath $vcvars)) { throw 'Visual C++ の開発環境が見つかりません' }
    $vcArch = if ($runtime -eq 'win-arm64') { 'amd64_arm64' } else { 'amd64' }
    $buildEnvironment = & cmd.exe /d /c "call `"$vcvars`" $vcArch >nul && set"
    if ($LASTEXITCODE -ne 0) { throw "Visual C++ 開発環境の初期化に失敗しました ($runtime)" }
    foreach ($entry in $buildEnvironment) {
        if ($entry -match '^(PATH|INCLUDE|LIB|LIBPATH)=(.*)$') {
            [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process')
        }
    }

    Write-Host "== publish: $runtime ==" -ForegroundColor Cyan
    Invoke-Native "dotnet publish ($runtime)" {
        # Native AOT は MSBuild の OS property で host OS を判定する。Codex 等の最小環境では
        # Windows 上でも OS 環境変数が無いことがあるため、Windows 専用スクリプト側で明示する。
        dotnet publish src/Ferry/Ferry.csproj -c Release -r $runtime -o $publishDir -p:OS=Windows_NT -p:IlcUseEnvironmentalTools=true
    }

    if (-not (Test-Path (Join-Path $publishDir 'Ferry.exe'))) {
        throw "Ferry.exe が publish 出力にありません ($runtime)"
    }

    Write-Host "== vpk pack + 署名: $runtime ==" -ForegroundColor Cyan
    # CI (velopack.yml) の Windows 系 vpk pack 引数 + --signTemplate (Sigilune 署名の追加分)
    # --icon: Setup.exe のアイコン (旧 CI は未指定でアイコン無し Setup.exe になっていた)
    Invoke-Native "vpk pack ($runtime)" {
        vpk pack `
            --packId Ferry `
            --packVersion $version `
            --mainExe Ferry.exe `
            --packDir $publishDir `
            --outputDir $ArtifactsDir `
            --channel $config.Channel `
            --icon (Join-Path $RepoRoot 'icon\app.ico') `
            --signTemplate $SignTemplate
    }
}
} finally {
    # 初期化・publish・packの失敗でも、未設定だった変数を含めて元へ戻す。
    foreach ($name in $originalBuildEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, ($originalBuildEnvironment[$name] ?? [NullString]::Value), 'Process')
    }
}
}

# 通常ビルドと UploadOnly は同じ実物検証を通す。
Assert-ReleaseArtifacts

if ($SkipUpload) {
    Write-Host "`n✅ -SkipUpload 指定のためここで終了。成果物: $ArtifactsDir" -ForegroundColor Green
    Get-ChildItem $ArtifactsDir | Format-Table Name, @{n='Size(MB)'; e={[math]::Round($_.Length/1MB,1)}}
    return
}

# ---- 2. R2 アップロード ----
# - manifest (releases.*.json) は必ず最後にアップロードする (CI rere #F-001 と同方針)。
#   manifest が先に上がると「manifest が指す nupkg が R2 に無い時間窓」ができ、
#   その間の更新チェックが 404 で失敗する
# - *.nupkg は put のみ (過去版は cleanup ステップが manifest 基準で削除)
Write-Host '== R2 アップロード ==' -ForegroundColor Cyan
Assert-ImmutableRemotePackages
$files = Get-ChildItem $ArtifactsDir -File
$ordered = @($files | Where-Object { $_.Name -notmatch '^(?:releases\..*\.json|assets\..*\.json|RELEASES(?:-.*)?)$' }) +
           @($files | Where-Object { $_.Name -match '^(?:releases\..*\.json|assets\..*\.json|RELEASES(?:-.*)?)$' })
$uploaded = 0
foreach ($f in $ordered) {
    Assert-LocalPath $f.FullName $WorkDir
    if (-not $VerifiedHashes.ContainsKey($f.Name) -or (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash -ne $VerifiedHashes[$f.Name]) {
        throw "検証後に成果物が変わりました: $($f.Name)"
    }
    Write-Host "  ↑ $($f.Name)"
    $immutable = $f.Name -match '(?:\.nupkg$|[-_.]\d+\.\d+\.\d+(?:[-_.]|$))'
    $cacheControl = if ($immutable) { 'public, max-age=31536000, immutable' } else { 'no-cache, must-revalidate' }
    $contentType = switch -Regex ($f.Name) { '\.json$' { 'application/json'; break }; '^RELEASES' { 'text/plain'; break }; '\.(zip|nupkg)$' { 'application/zip'; break }; default { 'application/octet-stream' } }
    Publish-R2Object -AccountId $AccountId -Bucket $Bucket -FilePath $f.FullName -Immutable:$immutable -CacheControl $cacheControl -ContentType $contentType | Out-Null
    $uploaded++
}
Write-Host "✅ R2 アップロード完了: $uploaded ファイル"

# ---- 2.5 Cloudflare エッジキャッシュのパージ ----
# 固定名ファイル (Setup.exe / Portable.zip / RELEASES / releases.*.json / assets.*.json) は
# 毎リリースで中身が変わるのに URL が不変。CDN エッジが旧版を Cache-Control の max-age 分保持するため、
# 古いキャッシュが残っているかを取得した内容のハッシュで確認し、不一致の URL だけパージする。
# バージョン付き nupkg は URL が一意 (旧キャッシュなし) のためパージ不要。
Write-Host '== Cloudflare キャッシュパージ ==' -ForegroundColor Cyan
$cfHeaders = @{ Authorization = "Bearer $($env:CLOUDFLARE_API_TOKEN)" }
$zoneName = ([uri]$BaseUrl).Host -replace '^[^.]+\.', ''   # <sub>.kagayoi.com → kagayoi.com (apex)
$zoneResp = Invoke-RestMethod -Uri "https://api.cloudflare.com/client/v4/zones?name=$zoneName" -Headers $cfHeaders -TimeoutSec 30
if (-not $zoneResp.success -or @($zoneResp.result).Count -ne 1 -or $zoneResp.result[0].account.id -ne $AccountId) { throw "Cloudflare zone '$zoneName' を対象アカウントで一意に確認できませんでした" }
$zoneId = $zoneResp.result[0].id
$purgeUrls = @(foreach ($artifact in (Get-ChildItem $ArtifactsDir -File | Where-Object { $_.Name -notlike '*.nupkg' })) {
    $artifactUrl = "$BaseUrl/$($artifact.Name)"
    $response = Invoke-WebRequest -Uri "${artifactUrl}?verify=$([Guid]::NewGuid().ToString('N'))" -Headers @{ 'Cache-Control' = 'no-cache' } -TimeoutSec 120
    $bytes = if ($response.Content -is [byte[]]) { $response.Content } else { [Text.Encoding]::UTF8.GetBytes($response.Content) }
    $remoteHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    if ($remoteHash -ne (Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256).Hash) { $artifactUrl }
})
if ($purgeUrls.Count -gt 0) {
    $purgeBody = [PSCustomObject]@{ files = $purgeUrls } | ConvertTo-Json -Compress
    $purgeResp = Invoke-RestMethod -Method Post -Uri "https://api.cloudflare.com/client/v4/zones/$zoneId/purge_cache" `
        -Headers $cfHeaders -ContentType 'application/json' -Body $purgeBody -TimeoutSec 30
    if (-not $purgeResp.success) { throw "Cloudflare キャッシュパージに失敗しました: $($purgeResp.errors | ConvertTo-Json -Compress)" }
    Write-Host "  ✅ パージ: $($purgeUrls.Count) URL"
    $purgeUrls | ForEach-Object { Write-Host "     $_" }
} else {
    Write-Host '  パージ対象なし'
}

# ---- 3. 配信確認 (CDN/edge 伝播チェック) ----
Write-Host '== 配信確認 ==' -ForegroundColor Cyan
foreach ($runtime in $Runtimes) {
    $channel = $RuntimeMatrix[$runtime].Channel
    $url = "$BaseUrl/releases.$channel.json"
    $resp = Invoke-WebRequest -Uri $url -TimeoutSec 30 -MaximumRetryCount 3 -RetryIntervalSec 5
    Write-Host "  $url → HTTP $($resp.StatusCode) ($($resp.RawContentLength) bytes)"
}

# ---- 4. 旧バージョンのクリーンアップ (直近 N バージョン保持) ----
# keep set = ローカル artifacts の manifest (今アップロードしたもの) +
#            今回ビルドしていないチャンネル (osx / linux 等) の manifest を R2 から取得。
# 削除対象は「manifest 外 かつ バージョン文字列付き かつ 直近 N バージョン以外」。
# 固定ファイル名 (Setup.exe / Portable.zip / AppImage / RELEASES* / assets.*.json /
# releases.*.json) はバージョン文字列を含まないので対象外 = 安全。
# ⚠️ 旧実装は '*.nupkg' だけを削除対象にしていたため、package.yml が生成する
#    バージョン付き配布物 (ferry_x.y.z.zip / .deb / .rpm / ferry-x.y.z.AppImage) が
#    永久に残り、1.0.31〜1.0.69 の 39 世代 351 個 = 7.2GB が R2 に滞留していた
#    (2026-07 実測。R2 含有枠 10GB の 72% を 1 バケットで消費)。
Write-Host '== 旧バージョンクリーンアップ ==' -ForegroundColor Cyan
$keep = @{}
$manifests = Get-ChildItem $ArtifactsDir -Filter 'releases.*.json'
if (-not $manifests) { throw 'artifacts に releases.*.json が見つかりません' }
foreach ($m in $manifests) {
    foreach ($asset in (Get-Content $m.FullName -Raw | ConvertFrom-Json).Assets) {
        if ($asset.FileName) { $keep[$asset.FileName] = $true }
    }
}

# 今回リリースしていないチャンネルの manifest を取得して keep set に追加。
# 取得できないチャンネルがあると keep set が不完全 = 配信中 nupkg を誤削除しうるので
# 安全側に倒して cleanup ごと中止する (アップロード済みのリリース自体は有効)
$releasedChannels = @($Runtimes | ForEach-Object { $RuntimeMatrix[$_].Channel })
foreach ($channel in ($AllChannels | Where-Object { $releasedChannels -notcontains $_ })) {
    $url = "$BaseUrl/releases.$channel.json"
    try {
        # クエリで CDN キャッシュをバイパス。R2 は text 系でない Content-Type で返すことが
        # あり、その場合 .Content は byte[] になるため UTF-8 デコードしてから JSON parse する
        $resp = Invoke-WebRequest -Uri "${url}?_=$([Guid]::NewGuid().ToString('N'))" `
            -Headers @{ 'Cache-Control' = 'no-cache' } -TimeoutSec 30 -MaximumRetryCount 3 -RetryIntervalSec 5
        $raw = $resp.Content
        if ($raw -is [byte[]]) { $raw = [System.Text.Encoding]::UTF8.GetString($raw) }
        $remote = $raw | ConvertFrom-Json
    } catch {
        throw "他チャンネル manifest の取得に失敗 ($url)。keep set が不完全なため cleanup を中止します — $($_.Exception.Message)"
    }
    $assetsProp = $remote.PSObject.Properties['Assets']
    if (-not $assetsProp) {
        throw "他チャンネル manifest の形式が想定外です ($url)。keep set が不完全なため cleanup を中止します"
    }
    if ($assetsProp.Value) {
        foreach ($asset in $assetsProp.Value) {
            if ($asset.FileName) { $keep[$asset.FileName] = $true }
        }
    }
}
Write-Host "  保持対象 nupkg: $($keep.Count) 件 (全 $($AllChannels.Count) チャンネル)"

$api = "https://api.cloudflare.com/client/v4/accounts/$AccountId/r2/buckets/$Bucket"
$headers = @{ Authorization = "Bearer $($env:CLOUDFLARE_API_TOKEN)" }

$allKeys = [System.Collections.Generic.List[string]]::new()
$cursor = ''
while ($true) {
    $uri = "$api/objects?per_page=1000" + $(if ($cursor) { "&cursor=$cursor" })
    $resp = Invoke-RestMethod -Uri $uri -Headers $headers -TimeoutSec 30
    foreach ($obj in $resp.result) { $allKeys.Add($obj.key) }
    # 全件 1 ページに収まると result_info が省略される (StrictMode 下では直接参照が throw)
    $info = $resp.PSObject.Properties['result_info']
    if (-not $info -or -not $info.Value) { break }
    $truncated = $info.Value.PSObject.Properties['is_truncated']
    if (-not $truncated -or -not $truncated.Value) { break }
    $cursorProp = $info.Value.PSObject.Properties['cursor']
    $cursor = if ($cursorProp) { $cursorProp.Value } else { '' }
    if (-not $cursor) { break }
}

# 全プロジェクト共通の保持ポリシー: 直近 2 バージョン。
$KeepVersionCount = 2
$versionPattern = '(\d+\.\d+\.\d+)'
$allVersions = @(
    $allKeys | ForEach-Object {
        $m = [regex]::Match($_, $versionPattern)
        if ($m.Success) { $m.Groups[1].Value }
    } | Sort-Object -Property { [version]$_ } -Unique
)
$keepVersions = @($allVersions | Select-Object -Last $KeepVersionCount)
Write-Host "  保持バージョン: $($keepVersions -join ', ') (全 $($allVersions.Count) 世代)"

$toDelete = $allKeys | Where-Object {
    # manifest が参照するファイルは絶対保持 (消すと自動更新が壊れる)
    if ($keep.ContainsKey($_)) { return $false }
    # 固定ファイル名はバージョン文字列を含まない = 毎リリース上書きなので保持
    $m = [regex]::Match($_, $versionPattern)
    if (-not $m.Success) { return $false }
    return $keepVersions -notcontains $m.Groups[1].Value
}
if (-not $toDelete) {
    Write-Host '  ✅ 削除対象なし'
} else {
    $deleted = 0; $failed = 0
    foreach ($key in $toDelete) {
        $encoded = [uri]::EscapeDataString($key)
        try {
            Invoke-RestMethod -Method Delete -Uri "$api/objects/$encoded" -Headers $headers -TimeoutSec 30 | Out-Null
            Write-Host "  🗑️  $key"
            $deleted++
        } catch {
            Write-Warning "  削除失敗: $key — $($_.Exception.Message)"
            $failed++
        }
    }
    Write-Host "  🧹 クリーンアップ: $deleted 削除 / $failed 失敗"
    # 全件失敗は token 権限等の異常なので fail (一部失敗は次回リリースで再試行される)
    if ($failed -gt 0 -and $deleted -eq 0) { throw '旧 nupkg の削除がすべて失敗しました。API token の権限を確認してください。' }
}

Write-Host "`n🎉 リリース完了: v$version → $BaseUrl (Windows チャンネルのみ。macOS/Linux は release.yml が配信)" -ForegroundColor Green

#requires -Version 7.2
# 公開関数の互換入口。manifest・path・転送の正本は同梱 R2Transfer.Core.cs。
. "$PSScriptRoot/R2Distribution.Common.ps1"
function Get-R2ArtifactIdentity {
    param([string]$AccountId, [string]$Bucket, [string]$Name, [string]$Repository, [string]$Commit, [string]$RunId, [string]$RunAttempt)
    return [Kagayoi.R2.Transfer]::ArtifactIdentity($AccountId, $Bucket, $Name, $Repository, $Commit, $RunId, $RunAttempt)
}
function Get-R2ArtifactPrefix($Identity) { return [Kagayoi.R2.Transfer]::ArtifactPrefix($Identity) }
function Assert-R2ArtifactRelativePath([string]$Path) { [Kagayoi.R2.Transfer]::AssertRelativePath($Path) }
function Assert-R2ArtifactNoLinks([string]$Path) { [Kagayoi.R2.Transfer]::AssertNoLinks($Path) }
function Get-R2ArtifactFiles([string[]]$Paths) {
    $baseDirectory = [Kagayoi.R2.Transfer]::ArtifactBaseDirectory((Get-Location).Provider.Name, $ExecutionContext.SessionState.Path.CurrentFileSystemLocation.ProviderPath, $Paths)
    return [Kagayoi.R2.Transfer]::ArtifactFiles($Paths, $baseDirectory)
}
function Get-R2ArtifactObjectAddress([string]$AccountId, [string]$Bucket, [string]$Key) {
    $address = [Kagayoi.R2.Transfer]::ObjectAddress($AccountId, $Bucket, $Key)
    return @{ Uri = $address.Uri; Path = $address.Path }
}
function Get-R2ArtifactObject {
    param([Net.Http.HttpClient]$Client, [hashtable]$Credentials, [string]$AccountId, [string]$Bucket, [string]$Key,
        [IO.Stream]$Output, [long]$MaximumSize, [long]$ExpectedSize = -1, [string]$ExpectedHash)
    [Kagayoi.R2.Transfer]::GetArtifactObject($Client, $Credentials, $AccountId, $Bucket, $Key, $Output, $MaximumSize, $ExpectedSize, $ExpectedHash)
}
function Publish-R2ArtifactManifest {
    param([Net.Http.HttpClient]$Client, [hashtable]$Credentials, [string]$AccountId, [string]$Bucket, [string]$Key, [byte[]]$Bytes)
    [Kagayoi.R2.Transfer]::PublishManifest($Client, $Credentials, $AccountId, $Bucket, $Key, $Bytes)
}
function Invoke-R2ArtifactSafeFailure([Exception]$Failure) { throw [Kagayoi.R2.Transfer]::SafeFailure($Failure) }
function Publish-R2Artifact {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$AccountId, [Parameter(Mandatory)][string]$Bucket,
        [Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string[]]$Path,
        [string]$ApiToken = $env:CLOUDFLARE_API_TOKEN, [string]$AccessKeyId = $env:AWS_ACCESS_KEY_ID, [string]$SecretAccessKey = $env:AWS_SECRET_ACCESS_KEY,
        [string]$Repository = $env:GITHUB_REPOSITORY, [string]$Commit = $env:GITHUB_SHA,
        [string]$RunId = $env:GITHUB_RUN_ID, [string]$RunAttempt = $env:GITHUB_RUN_ATTEMPT)
    try {
        $baseDirectory = [Kagayoi.R2.Transfer]::ArtifactBaseDirectory((Get-Location).Provider.Name, $ExecutionContext.SessionState.Path.CurrentFileSystemLocation.ProviderPath, $Path)
        $result = [Kagayoi.R2.Transfer]::PublishArtifact($AccountId, $Bucket, $Name, $Path, $ApiToken, $AccessKeyId, $SecretAccessKey,
            $Repository, $Commit, $RunId, $RunAttempt, $PSBoundParameters.ContainsKey('AccessKeyId'), $PSBoundParameters.ContainsKey('SecretAccessKey'),
            [Func[Net.Http.HttpClient]]{ New-R2DistributionClient }, $baseDirectory)
        return [pscustomobject]@{ Name = $result.Name; Count = $result.Count; ManifestKey = $result.ManifestKey }
    } catch { throw [Kagayoi.R2.Transfer]::SafeFailure($_.Exception) }
}
function Receive-R2Artifact {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$AccountId, [Parameter(Mandatory)][string]$Bucket,
        [Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Destination,
        [string]$ApiToken = $env:CLOUDFLARE_API_TOKEN, [string]$AccessKeyId = $env:AWS_ACCESS_KEY_ID, [string]$SecretAccessKey = $env:AWS_SECRET_ACCESS_KEY,
        [string]$Repository = $env:GITHUB_REPOSITORY, [string]$Commit = $env:GITHUB_SHA,
        [string]$RunId = $env:GITHUB_RUN_ID, [string]$RunAttempt = $env:GITHUB_RUN_ATTEMPT)
    try {
        $baseDirectory = [Kagayoi.R2.Transfer]::ArtifactBaseDirectory((Get-Location).Provider.Name, $ExecutionContext.SessionState.Path.CurrentFileSystemLocation.ProviderPath, [string[]]@($Destination))
        $result = [Kagayoi.R2.Transfer]::ReceiveArtifact($AccountId, $Bucket, $Name, $Destination, $ApiToken, $AccessKeyId, $SecretAccessKey,
            $Repository, $Commit, $RunId, $RunAttempt, $PSBoundParameters.ContainsKey('AccessKeyId'), $PSBoundParameters.ContainsKey('SecretAccessKey'),
            [Func[Net.Http.HttpClient]]{ New-R2DistributionClient }, $baseDirectory)
        return [pscustomobject]@{ Name = $result.Name; Count = $result.Count; Destination = $result.Destination }
    } catch { throw [Kagayoi.R2.Transfer]::SafeFailure($_.Exception) }
}

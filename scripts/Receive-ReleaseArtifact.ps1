[CmdletBinding()]
param([Parameter(Mandatory)][string[]]$Name, [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][ValidatePattern('^[1-9][0-9]*$')][string]$RunAttempt)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'R2Artifact.Common.ps1')
$target = [IO.Path]::GetFullPath($Destination)
Assert-R2ArtifactNoLinks $target
New-Item -ItemType Directory -Path $target -Force | Out-Null
foreach ($artifactName in $Name) {
    $stage = Join-Path $env:RUNNER_TEMP ('r2-artifact-' + [Guid]::NewGuid().ToString('N'))
    Receive-R2Artifact -AccountId $env:R2_ARTIFACT_ACCOUNT_ID -Bucket $env:R2_ARTIFACT_BUCKET -Name $artifactName -Destination $stage -RunAttempt $RunAttempt -AccessKeyId $env:R2_ARTIFACT_ACCESS_KEY_ID -SecretAccessKey $env:R2_ARTIFACT_SECRET_ACCESS_KEY | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File -Force) {
        $relative = [IO.Path]::GetRelativePath($stage, $file.FullName)
        $output = [IO.Path]::GetFullPath((Join-Path $target $relative))
        if (-not $output.StartsWith($target.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)) { throw 'artifact path escaped destination' }
        Assert-R2ArtifactNoLinks $output
        if (Test-Path -LiteralPath $output) {
            if (-not (Test-Path -LiteralPath $output -PathType Leaf) -or (Get-FileHash -LiteralPath $output).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw "artifact collision: $relative" }
            continue
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $output
    }
}

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$toolRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $toolRoot '..\..'))
$projectFile = Join-Path $toolRoot 'src\ProjectSync.Desktop\ProjectSync.Desktop.csproj'
$artifactsRoot = Join-Path $toolRoot 'artifacts'
$distributionRoot = Join-Path $toolRoot 'dist'
$safeRepository = $repositoryRoot.Replace('\', '/')

$sourceSha = (& git -c "safe.directory=$safeRepository" -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceSha -notmatch '^[0-9a-f]{40}$') {
    throw 'Source commit SHA could not be determined.'
}

$workingChanges = @(& git -c "safe.directory=$safeRepository" -C $repositoryRoot status --porcelain=v1)
if ($LASTEXITCODE -ne 0 -or $workingChanges.Count -ne 0) {
    throw 'Commit or set aside source changes before packaging so the ZIP has an exact source identity.'
}

New-Item -ItemType Directory -Path $artifactsRoot, $distributionRoot -Force | Out-Null
$stageRoot = Join-Path $artifactsRoot ('portable-' + [Guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $stageRoot 'ProjectSync'
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
$zipPath = $null
$ownsOutputName = $false
$packageSucceeded = $false

try {
    & dotnet publish $projectFile `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $packageRoot `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet publish failed.'
    }

    $executable = Join-Path $packageRoot 'ProjectSync.Desktop.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw 'The portable executable was not produced.'
    }

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README_JA.md') -Destination (Join-Path $packageRoot 'README_JA.md')
    @(
        "Source commit: $sourceSha"
        'Target: Windows x64'
        'Deployment: self-contained portable preview'
        'Signing: unsigned'
    ) | Set-Content -LiteralPath (Join-Path $packageRoot 'BUILD-INFO.txt') -Encoding utf8

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $nonce = [Guid]::NewGuid().ToString('N').Substring(0, 6)
    $packageName = "ProjectSync-preview-win-x64-$($sourceSha.Substring(0, 12))-$stamp-$nonce.zip"
    $zipPath = Join-Path $distributionRoot $packageName
    if (Test-Path -LiteralPath $zipPath) {
        throw "Package already exists: $zipPath"
    }
    $ownsOutputName = $true

    Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal
    Add-Type -AssemblyName System.IO.Compression
    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        if (-not ($archive.Entries | Where-Object FullName -eq 'ProjectSync/ProjectSync.Desktop.exe')) {
            throw 'ZIP is missing ProjectSync.Desktop.exe.'
        }
    }
    finally {
        $archive.Dispose()
    }

    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $packageName" | Set-Content -LiteralPath ($zipPath + '.sha256') -Encoding ascii
    $packageSucceeded = $true
    Write-Output $zipPath
    Write-Output ($zipPath + '.sha256')
}
finally {
    $resolvedDistribution = [IO.Path]::GetFullPath($distributionRoot)
    if (-not $packageSucceeded -and $ownsOutputName -and $zipPath) {
        $resolvedZip = [IO.Path]::GetFullPath($zipPath)
        if ($resolvedZip.StartsWith($resolvedDistribution + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            foreach ($generatedPath in @($resolvedZip, ($resolvedZip + '.sha256'))) {
                if (Test-Path -LiteralPath $generatedPath) {
                    Remove-Item -LiteralPath $generatedPath -Force
                }
            }
        }
    }

    $resolvedArtifacts = [IO.Path]::GetFullPath($artifactsRoot)
    $resolvedStage = [IO.Path]::GetFullPath($stageRoot)
    if ($resolvedStage.StartsWith($resolvedArtifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedStage)) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}

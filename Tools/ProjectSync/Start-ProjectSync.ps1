[CmdletBinding()]
param(
    [switch]$BuildOnly,
    [string]$TargetProjectPath
)

$ErrorActionPreference = 'Stop'
$toolRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $toolRoot '..\..'))
$targetProjectRoot = if ([string]::IsNullOrWhiteSpace($TargetProjectPath)) {
    $repositoryRoot
} else {
    [IO.Path]::GetFullPath($TargetProjectPath)
}
$projectFile = Join-Path $toolRoot 'src\ProjectSync.Desktop\ProjectSync.Desktop.csproj'
$executable = Join-Path $toolRoot 'src\ProjectSync.Desktop\bin\Debug\net8.0-windows\ProjectSync.Desktop.exe'

try {
    $running = @(Get-Process -Name ProjectSync.Desktop -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        throw 'ProjectSync is running. Close its window and retry. The launcher will not terminate it or discard changes.'
    }

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw '.NET 8 SDK was not found on this development PC.'
    }

    foreach ($folder in @('Assets', 'Packages', 'ProjectSettings')) {
        if (-not (Test-Path -LiteralPath (Join-Path $targetProjectRoot $folder) -PathType Container)) {
            throw "The selected target is not a Unity project: $targetProjectRoot"
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $targetProjectRoot '.git'))) {
        throw "The selected target is not a Git checkout: $targetProjectRoot"
    }

    & dotnet build $projectFile --configuration Debug --nologo
    if ($LASTEXITCODE -ne 0) {
        throw 'ProjectSync build failed. Check the build errors above.'
    }

    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw 'ProjectSync.Desktop.exe was not found after the build.'
    }

    if ($BuildOnly) {
        Write-Output "Build complete: $executable"
        exit 0
    }

    $argumentLine = '--project "{0}"' -f $targetProjectRoot
    $process = Start-Process -FilePath $executable -WorkingDirectory $targetProjectRoot -ArgumentList $argumentLine -PassThru
    Write-Output "ProjectSync started (PID $($process.Id)): $executable"
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}

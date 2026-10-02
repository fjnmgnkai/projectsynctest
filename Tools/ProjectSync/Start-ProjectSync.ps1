[CmdletBinding()]
param(
    [switch]$BuildOnly
)

$ErrorActionPreference = 'Stop'
$toolRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $toolRoot '..\..'))
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

    $argumentLine = '--project "{0}"' -f $repositoryRoot
    $process = Start-Process -FilePath $executable -WorkingDirectory $repositoryRoot -ArgumentList $argumentLine -PassThru
    Write-Output "ProjectSync started (PID $($process.Id)): $executable"
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}

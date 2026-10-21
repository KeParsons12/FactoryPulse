[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot 'FactoryPulse.sln'
$launcherExecutable = Join-Path $repositoryRoot 'src\FactoryPulse.DemoLauncher\bin\Debug\net10.0\FactoryPulse.DemoLauncher.exe'

Push-Location $repositoryRoot
try {
    Write-Host '[LAUNCHER] Building FactoryPulse...'
    & dotnet build $solutionPath --configuration Debug --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) {
        throw "FactoryPulse build failed with exit code $LASTEXITCODE."
    }

    & $launcherExecutable --repo-root $repositoryRoot
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}

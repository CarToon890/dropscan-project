$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '../..')
$fixtureRoot = Join-Path (Get-Location) 'artifacts/phase2-qa'
$targetPath = Join-Path $fixtureRoot 'junction-target'
$junctionPath = Join-Path $fixtureRoot 'junction-root'
New-Item -ItemType Directory -Force -Path $targetPath | Out-Null
if (-not (Test-Path -LiteralPath $junctionPath)) {
    New-Item -ItemType Junction -Path $junctionPath -Target $targetPath | Out-Null
}
dotnet restore tests/PhaseTwoSmoke/PhaseTwoSmoke.csproj --configfile NuGet.Config
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet build tests/PhaseTwoSmoke/PhaseTwoSmoke.csproj -c Release --no-restore -o artifacts/phase2-tests
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet artifacts/phase2-tests/PhaseTwoSmoke.dll
exit $LASTEXITCODE

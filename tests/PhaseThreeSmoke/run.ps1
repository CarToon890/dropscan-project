$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '../..')
dotnet restore tests/PhaseThreeSmoke/PhaseThreeSmoke.csproj --configfile NuGet.Config
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet build tests/PhaseThreeSmoke/PhaseThreeSmoke.csproj -c Release --no-restore -o artifacts/phase3-tests
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet artifacts/phase3-tests/PhaseThreeSmoke.dll
exit $LASTEXITCODE

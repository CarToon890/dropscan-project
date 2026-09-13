$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '../..')
dotnet restore tests/ApprovalSmoke/ApprovalSmoke.csproj --configfile NuGet.Config
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet build tests/ApprovalSmoke/ApprovalSmoke.csproj -c Release --no-restore -o artifacts/approval-tests
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet artifacts/approval-tests/ApprovalSmoke.dll
exit $LASTEXITCODE

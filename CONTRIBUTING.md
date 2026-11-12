# Contributing

Thanks for helping improve DropScan. Please open an issue before a large feature change so its scope and security impact can be discussed.

## Development setup

- Windows 11 x64 (Windows 10 support is limited to LTSC/Enterprise editions supported by .NET 10)
- .NET 10 SDK
- Git

Restore and build:

```powershell
dotnet restore DropScan.csproj --configfile NuGet.Config
dotnet build DropScan.csproj --configuration Release --no-restore
```

Run the smoke suites on Windows:

```powershell
$projects = @(
  'tests/TransferSmoke/TransferSmoke.csproj',
  'tests/PhaseTwoSmoke/PhaseTwoSmoke.csproj',
  'tests/PhaseThreeSmoke/PhaseThreeSmoke.csproj',
  'tests/ApprovalSmoke/ApprovalSmoke.csproj',
  'tests/DesktopSmoke/DesktopSmoke.csproj'
)
foreach ($project in $projects) {
  dotnet restore $project --configfile NuGet.Config
  if ($LASTEXITCODE -ne 0) { throw "Restore failed: $project" }
  dotnet run --project $project --configuration Release --no-restore
  if ($LASTEXITCODE -ne 0) { throw "Smoke suite failed: $project" }
}
```

## Change expectations

- Keep source files local to the sender; do not add hosted file storage or telemetry without an explicit design discussion.
- Do not put share tokens, PINs, cookies, receiver IPs, or local file paths in logs, history, screenshots, or test output.
- For transfer changes, cover authorization, expiry, cancellation, disconnects, file changes, and HTTP Range behavior where applicable.
- Keep LAN and Tunnel behavior explicit. Never silently fall back from public HTTPS to LAN HTTP.
- Update `README.md`, `PRIVACY.md`, and `WORK_PLAN.md` when user-visible behavior, network handling, or data retention changes.
- Run the relevant smoke suites and report which device/network scenarios were or were not exercised.


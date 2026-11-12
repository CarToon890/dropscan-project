param(
    [ValidateSet("win-x64")]
    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$outputDirectory = Join-Path $repoRoot "artifacts\publish\$RuntimeIdentifier"
$packageDirectory = Join-Path $repoRoot "artifacts\packages"
$packagePath = Join-Path $packageDirectory "DropScan-$RuntimeIdentifier.zip"

if (Test-Path -LiteralPath $outputDirectory) {
    throw "Publish output already exists; move it aside or remove it before publishing: $outputDirectory"
}
if (Test-Path -LiteralPath $packagePath) {
    throw "Package already exists; move it aside before creating a new package: $packagePath"
}

New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

& dotnet publish (Join-Path $repoRoot "DropScan.csproj") `
    --configuration Release `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --output $outputDirectory `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:NuGetAudit=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$executable = Join-Path $outputDirectory "DropScan.exe"
if (!(Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Self-contained executable was not produced: $executable"
}

Copy-Item -LiteralPath (Join-Path $repoRoot "packaging\README-user.md") -Destination $outputDirectory
Compress-Archive -Path (Join-Path $outputDirectory "*") -DestinationPath $packagePath

$exeInfo = Get-Item -LiteralPath $executable
$packageInfo = Get-Item -LiteralPath $packagePath
Write-Output "Executable: $($exeInfo.FullName) ($($exeInfo.Length) bytes)"
Write-Output "Package: $($packageInfo.FullName) ($($packageInfo.Length) bytes)"


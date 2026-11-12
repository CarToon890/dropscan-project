param(
    [ValidateSet("win-x64")]
    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$outputDirectory = Join-Path $repoRoot "artifacts\publish\$RuntimeIdentifier"
$packageDirectory = Join-Path $repoRoot "artifacts\packages"
$packagePath = Join-Path $packageDirectory "DropScan-$RuntimeIdentifier.zip"
$checksumPath = "$packagePath.sha256"

if (Test-Path -LiteralPath $outputDirectory) {
    throw "Publish output already exists; move it aside or remove it before publishing: $outputDirectory"
}
if (Test-Path -LiteralPath $packagePath) {
    throw "Package already exists; move it aside before creating a new package: $packagePath"
}
if (Test-Path -LiteralPath $checksumPath) {
    throw "Checksum already exists; move it aside before creating a new package: $checksumPath"
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
    -p:NuGetAudit=true

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$executable = Join-Path $outputDirectory "DropScan.exe"
if (!(Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Self-contained executable was not produced: $executable"
}

Copy-Item -LiteralPath (Join-Path $repoRoot "packaging\README-user.md") -Destination $outputDirectory
Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination (Join-Path $outputDirectory "LICENSE-DropScan.txt")
Copy-Item -LiteralPath (Join-Path $repoRoot "packaging\THIRD-PARTY-NOTICES.md") -Destination $outputDirectory
$dotnetRoot = Split-Path (Get-Command dotnet).Source
foreach ($notice in @{
    "LICENSE.txt" = "DOTNET-LICENSE.txt"
    "ThirdPartyNotices.txt" = "DOTNET-THIRD-PARTY-NOTICES.txt"
}.GetEnumerator()) {
    $sourceNotice = Join-Path $dotnetRoot $notice.Key
    if (!(Test-Path -LiteralPath $sourceNotice -PathType Leaf)) {
        throw "Required .NET redistribution notice not found: $sourceNotice"
    }
    Copy-Item -LiteralPath $sourceNotice -Destination (Join-Path $outputDirectory $notice.Value)
}
Compress-Archive -Path (Join-Path $outputDirectory "*") -DestinationPath $packagePath
$checksum = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $checksumPath -Value "$checksum  $(Split-Path -Leaf $packagePath)" -Encoding ascii

$exeInfo = Get-Item -LiteralPath $executable
$packageInfo = Get-Item -LiteralPath $packagePath
Write-Output "Executable: $($exeInfo.FullName) ($($exeInfo.Length) bytes)"
Write-Output "Package: $($packageInfo.FullName) ($($packageInfo.Length) bytes)"
Write-Output "SHA-256: $checksum"


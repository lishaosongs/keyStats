# KeyStats Windows Build Script
# Usage: .\build.ps1 [Release|Debug] [-SyncServiceBaseUrl <https-url>] [-Version <x.y.z>]

param(
    [string]$Configuration = "Release",
    [string]$SyncServiceBaseUrl = "",
    [string]$Version = ""
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = "Stop"

# Resolve the script directory.
$ScriptDir = if ($PSScriptRoot) {
    $PSScriptRoot
} else {
    Split-Path -Parent $MyInvocation.MyCommand.Path
}

if (-not $ScriptDir) {
    Write-Host "Error: Cannot determine script directory. Please run this script from its location." -ForegroundColor Red
    exit 1
}

$ProjectDir = Join-Path $ScriptDir "KeyStats"
$ProjectFile = Join-Path $ProjectDir "KeyStats.csproj"
$OutputDir = Join-Path $ScriptDir "publish"
$DistDir = Join-Path $ScriptDir "dist"

# Version: explicit -Version wins; otherwise the nearest reachable v* tag of the current commit;
# git unavailable or no tag (source snapshot) falls back to the csproj. Tags only label the build,
# they are never written back to source.
function Resolve-BuildVersion {
    param(
        [string]$ExplicitVersion,
        [string]$ProjectFile,
        [string]$RepoDir
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitVersion)) {
        $version = $ExplicitVersion
    }
    else {
        $version = $null
        try {
            $tag = & git -C $RepoDir describe --tags --abbrev=0 --match "v[0-9]*" 2>$null
            if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($tag)) {
                $version = $tag.Trim()
            }
        }
        catch {
            # git is missing or the directory is not a repository.
        }

        if ([string]::IsNullOrWhiteSpace($version)) {
            $match = Select-String -Path $ProjectFile -Pattern '<Version>([^<]+)</Version>'
            $version = if ($match -and $match.Matches.Groups[1].Value) {
                $match.Matches.Groups[1].Value.Trim()
            }
            else {
                "1.0.0"
            }
        }
    }

    $normalized = $version.Trim().TrimStart('v', 'V')
    if ($normalized -notmatch '^\d+(\.\d+){1,3}(-[0-9A-Za-z.-]+)?$') {
        throw "Invalid version '$normalized'. Expected 2-4 numeric parts with optional prerelease suffix, e.g. 1.55 or 1.55-beta."
    }

    return $normalized
}

try {
    $Version = Resolve-BuildVersion -ExplicitVersion $Version -ProjectFile $ProjectFile -RepoDir $ScriptDir
}
catch {
    Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

$BuildProperties = @("/p:Version=$Version")
$HasSyncServiceBaseUrl = -not [string]::IsNullOrWhiteSpace($SyncServiceBaseUrl)
if ($HasSyncServiceBaseUrl) {
    $BuildProperties += "/p:SyncServiceBaseUrl=$SyncServiceBaseUrl"
}

Write-Host "=== KeyStats Windows Build Script ===" -ForegroundColor Cyan
Write-Host "Configuration: $Configuration" -ForegroundColor Yellow
Write-Host "Version: $Version" -ForegroundColor Yellow
Write-Host "Target Framework: .NET Framework 4.8" -ForegroundColor Yellow
Write-Host "Sync service URL configured: $HasSyncServiceBaseUrl" -ForegroundColor Yellow
Write-Host ""

if (-not (Test-Path $ProjectFile)) {
    Write-Host "Error: Project file not found: $ProjectFile" -ForegroundColor Red
    exit 1
}

Write-Host "Cleaning previous builds..." -ForegroundColor Cyan

$processes = Get-Process -Name "KeyStats" -ErrorAction SilentlyContinue
if ($processes) {
    Write-Host "Stopping running KeyStats processes..." -ForegroundColor Yellow
    $processes | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

Start-Sleep -Seconds 0.5

if (Test-Path $OutputDir) {
    $retries = 3
    $retryCount = 0
    while ($retryCount -lt $retries) {
        try {
            Remove-Item -Path $OutputDir -Recurse -Force -ErrorAction Stop
            break
        }
        catch {
            $retryCount++
            if ($retryCount -lt $retries) {
                Write-Host "Retry ${retryCount}/${retries}: Waiting before retry..." -ForegroundColor Yellow
                Start-Sleep -Seconds 1
            }
            else {
                Write-Host "Warning: Could not remove $OutputDir. Some files may be locked." -ForegroundColor Yellow
            }
        }
    }
}

if (Test-Path $DistDir) {
    $retries = 3
    $retryCount = 0
    while ($retryCount -lt $retries) {
        try {
            Remove-Item -Path $DistDir -Recurse -Force -ErrorAction Stop
            break
        }
        catch {
            $retryCount++
            if ($retryCount -lt $retries) {
                Write-Host "Retry ${retryCount}/${retries}: Waiting before retry..." -ForegroundColor Yellow
                Start-Sleep -Seconds 1
            }
            else {
                Write-Host "Warning: Could not remove $DistDir. Some files may be locked." -ForegroundColor Yellow
            }
        }
    }
}

Write-Host "Restoring dependencies..." -ForegroundColor Cyan
Push-Location $ScriptDir
try {
    dotnet restore $ProjectFile
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Restore failed!" -ForegroundColor Red
        exit 1
    }
    Write-Host "Restore succeeded!" -ForegroundColor Green
}
finally {
    Pop-Location
}

Write-Host "Building project..." -ForegroundColor Cyan
Push-Location $ScriptDir
try {
    dotnet build $ProjectFile -c $Configuration @BuildProperties
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed!" -ForegroundColor Red
        exit 1
    }
    Write-Host "Build succeeded!" -ForegroundColor Green
}
finally {
    Pop-Location
}

Write-Host "Publishing build output..." -ForegroundColor Cyan
Write-Host "Target Framework: .NET Framework 4.8 (preinstalled on Windows 10/11)" -ForegroundColor Yellow
Write-Host "App size: about 5-10 MB, ready to use after extraction" -ForegroundColor Green

Push-Location $ScriptDir
try {
    dotnet build $ProjectFile -c $Configuration -o $OutputDir @BuildProperties
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed!" -ForegroundColor Red
        exit 1
    }

    $BinDir = Join-Path $ProjectDir "bin\$Configuration\net48"
    if (Test-Path $BinDir) {
        Get-ChildItem -Path $BinDir -Force | Where-Object { $_.Name -ne "win-x86" } | Copy-Item -Destination $OutputDir -Recurse -Force

        # 移除 publish 中不含任何文件（含隐藏文件）的空目录，避免历史遗留的空 Assets/runtimes 结构进入 ZIP
        Get-ChildItem -Path $OutputDir -Recurse -Directory -Force |
            Sort-Object { $_.FullName.Length } -Descending |
            Where-Object { -not (Get-ChildItem -Path $_.FullName -Recurse -File -Force) } |
            Remove-Item -Recurse -Force
    }

    Write-Host "Build succeeded!" -ForegroundColor Green
}
finally {
    Pop-Location
}

$PublishedExe = Join-Path $OutputDir "KeyStats.exe"
$PublishedProductVersion = (Get-Item $PublishedExe).VersionInfo.ProductVersion
if (($PublishedProductVersion -split '\+')[0] -ne $Version) {
    Write-Host "Error: Published KeyStats.exe ProductVersion '$PublishedProductVersion' does not match expected '$Version'." -ForegroundColor Red
    exit 1
}
Write-Host "Verified published version: $PublishedProductVersion" -ForegroundColor Green

Write-Host "Creating distribution package..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path $DistDir -Force | Out-Null

$ZipName = "KeyStats-Windows-$Version.zip"
$ZipPath = Join-Path $DistDir $ZipName

$TempDir = Join-Path $DistDir "KeyStats-$Version"
New-Item -ItemType Directory -Path $TempDir -Force | Out-Null

Write-Host "Copying files..." -ForegroundColor Cyan
Copy-Item -Path "$OutputDir\*" -Destination $TempDir -Recurse -Force

$ReadmeLines = @(
    "KeyStats for Windows",
    "Version: $Version",
    "",
    "Installation:",
    "1. Extract this ZIP file to any directory",
    "2. Run KeyStats.exe",
    "3. Grant necessary permissions on first run",
    "",
    "Note: This version uses .NET Framework 4.8, which is pre-installed on Windows 10/11.",
    "No additional installation required - ready to use!",
    "",
    "Data Storage:",
    "%LOCALAPPDATA%\KeyStats",
    "",
    "Uninstall:",
    "Simply delete the program folder. Data will remain in user data directory.",
    "",
    "System Requirements:",
    "- Windows 10 (1903+) or Windows 11",
    "- No .NET runtime installation required (uses pre-installed .NET Framework 4.8)"
)

$ReadmePath = Join-Path $TempDir "README.txt"
$ReadmeContent = $ReadmeLines -join "`r`n"
[System.IO.File]::WriteAllText($ReadmePath, $ReadmeContent, [System.Text.Encoding]::UTF8)

Write-Host "Creating ZIP file..." -ForegroundColor Cyan
if (Test-Path $ZipPath) {
    Remove-Item -Path $ZipPath -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($TempDir, $ZipPath)

Remove-Item -Path $TempDir -Recurse -Force

Write-Host ""
Write-Host "=== Build Complete ===" -ForegroundColor Green
Write-Host "Output file: $ZipPath" -ForegroundColor Cyan
Write-Host "File size: $([math]::Round((Get-Item $ZipPath).Length / 1MB, 2)) MB" -ForegroundColor Cyan
Write-Host ""
Write-Host "Published files location:" -ForegroundColor Yellow
Write-Host "  $OutputDir" -ForegroundColor White
Write-Host ""
Write-Host "Distribution package location:" -ForegroundColor Yellow
Write-Host "  $ZipPath" -ForegroundColor White

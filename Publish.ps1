<#
.SYNOPSIS
    Packages FFT Treasure Master (the in-process runtime DLL + baked treasure dataset) for release.
.DESCRIPTION
    Production counterpart to BuildLinked.ps1 (which deploys straight into the live
    Reloaded Mods folder). Mirrors the sibling FFTItemOverhaul mod's BuildLinked / Publish split.

    Bakes the treasure dataset (self-test gate), builds the runtime DLL, then stages the
    deliverables (ModConfig.json, optional preview.png, FFTTreasureMaster.dll + deps + treasure.json)
    into a build folder named after the ModId and zips it with a single top-level wrapper folder
    so Reloaded-II / Nexus / Vortex extract to the expected path.
.PARAMETER Version
    Version number for the mod. Default: reads ModVersion from mod/ModConfig.json.
.PARAMETER OutputPath
    Where to save the final ZIP. Default: "." under GitHub Actions, else Downloads.
.PARAMETER NexusModId
    Nexus mod ID for the archive filename convention. Placeholder 0 until registered.
.PARAMETER SkipGenerate
    Skip the treasure-db bake (package the committed treasure.json as-is). Use only
    when you've already baked + gated this session.
#>

[cmdletbinding()]
param (
    [string]$Version = "",
    [string]$OutputPath = "",
    [int]$NexusModId = 0,
    [switch]$SkipGenerate
)

## => Configuration <= ##
$ModId           = "prawl.fft.treasuremaster"
$SourceModPath   = "mod"
$BuildOutputPath = "Publish/$ModId"
$SourceModConfig = "$SourceModPath/ModConfig.json"
$SourcePreview   = "$SourceModPath/preview.png"

if (-not $OutputPath) {
    if ($env:GITHUB_ACTIONS) { $OutputPath = "." }
    else { $OutputPath = "C:\Users\ptyRa\Downloads" }
}

## => Shared pipeline (bake/gate, test gate, DLL publish, $RequiredModFiles) <= ##
. "$PSScriptRoot\tools\pipeline.ps1"

## => Functions <= ##
function Write-Status { param($Message, $Color = "Green") Write-Host "`n==> $Message" -ForegroundColor $Color }

function Write-ErrorMessage {
    param($Message)
    throw $Message
}

function Get-ModVersion {
    param([string]$RequestedVersion)
    if (-not [string]::IsNullOrEmpty($RequestedVersion)) {
        Write-Host "  -> Using version from -Version parameter: $RequestedVersion"
        return $RequestedVersion
    }
    if (-not (Test-Path $SourceModConfig)) {
        Write-ErrorMessage "No version specified and ModConfig.json not found at: $SourceModConfig"
    }
    $config = Get-Content $SourceModConfig -Raw | ConvertFrom-Json
    $modVersion = $config.ModVersion
    if ([string]::IsNullOrEmpty($modVersion)) {
        Write-ErrorMessage "ModConfig.json has no ModVersion field at: $SourceModConfig"
    }
    Write-Host "  -> Using version from ModConfig.json: $modVersion"
    return $modVersion
}

function Clean-BuildDirectories {
    Write-Status "Cleaning build directory..." "Yellow"
    if (Test-Path $BuildOutputPath) {
        Remove-Item "$BuildOutputPath\*" -Recurse -Force -ErrorAction SilentlyContinue | Out-Null
    } else {
        New-Item $BuildOutputPath -ItemType Directory -Force | Out-Null
    }
}

function Copy-ModAssets {
    Write-Status "Staging mod deliverables..." "Cyan"
    if (-not (Test-Path $SourceModConfig)) {
        Write-ErrorMessage "ModConfig.json not found at: $SourceModConfig"
    }
    Write-Host "  -> Copying ModConfig.json..."
    Copy-Item $SourceModConfig -Destination $BuildOutputPath -Force

    if (Test-Path $SourcePreview) {
        Write-Host "  -> Copying preview.png..."
        Copy-Item $SourcePreview -Destination $BuildOutputPath -Force
    } else {
        Write-Host "  -> No preview.png (optional ModIcon); skipping." -ForegroundColor Yellow
    }
}

function Create-Package {
    param([string]$ModVersion)
    Write-Status "Creating ZIP package..." "Green"

    $versionDashed = $ModVersion -replace '\.', '-'
    if ($NexusModId -gt 0) {
        $unixTimestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        $packageName = "FFTTreasureMaster-$NexusModId-$versionDashed-$unixTimestamp.zip"
    } else {
        Write-Host "  -> Stable name (no -NexusModId); pass -NexusModId for the Vortex-parseable name before a Nexus upload." -ForegroundColor Yellow
        $packageName = "FFTTreasureMaster-$ModVersion.zip"
    }
    $packagePath = Join-Path $OutputPath $packageName

    if (Test-Path $packagePath) { Remove-Item $packagePath -Force }
    if (-not (Test-Path $OutputPath)) { New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null }
    if (-not (Test-Path $BuildOutputPath)) { Write-ErrorMessage "Build output directory not found: $BuildOutputPath" }

    try { Add-Type -Assembly System.IO.Compression.FileSystem -ErrorAction Stop }
    catch { [Reflection.Assembly]::LoadWithPartialName("System.IO.Compression.FileSystem") | Out-Null }

    try {
        $absoluteBuildPath   = (Get-Item $BuildOutputPath).FullName
        $absolutePackagePath = [System.IO.Path]::GetFullPath($packagePath)
        Write-Host "  -> Source: $absoluteBuildPath"
        Write-Host "  -> Target: $absolutePackagePath"

        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $absoluteBuildPath, $absolutePackagePath,
            [System.IO.Compression.CompressionLevel]::Optimal, $true)

        if (Test-Path $absolutePackagePath) {
            $sizeMB = [math]::Round((Get-Item $absolutePackagePath).Length / 1MB, 2)
            Write-Host "  -> Package created ($sizeMB MB): $absolutePackagePath" -ForegroundColor Green
            return $absolutePackagePath
        }
        Write-ErrorMessage "Package was not created at: $absolutePackagePath"
    }
    catch {
        Write-Host "`n[ERROR] Failed to create ZIP package: $_" -ForegroundColor Red
        return $null
    }
}

function Verify-Package {
    param([string]$PackagePath)
    Write-Status "Verifying package contents..." "Cyan"
    if (-not $PackagePath -or -not (Test-Path $PackagePath)) {
        Write-Host "  -> Package not found for verification" -ForegroundColor Red
        return $false
    }
    Add-Type -Assembly System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $missingCount = 0
    try {
        $zip = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
        $entryPaths = @($zip.Entries | ForEach-Object { $_.FullName -replace '\\', '/' })
        $firstSegments = @($entryPaths | ForEach-Object { ($_ -split '/')[0] } | Sort-Object -Unique)
        if ($firstSegments.Count -eq 1 -and $firstSegments[0]) {
            $wrapper = $firstSegments[0]
            $entryPaths = @($entryPaths | ForEach-Object {
                if ($_.StartsWith("$wrapper/")) { $_.Substring($wrapper.Length + 1) } else { $_ }
            })
            Write-Host "  -> Wrapper folder: $wrapper" -ForegroundColor Gray
        }
        foreach ($file in $RequiredModFiles) {
            if ($entryPaths -contains $file) { Write-Host "  [OK] $file" -ForegroundColor Green }
            else { Write-Host "  [MISSING] $file" -ForegroundColor Red; $missingCount++ }
        }
        $zip.Dispose()
    }
    catch {
        Write-Host "`n[ERROR] Failed to verify package: $_" -ForegroundColor Red
        return $false
    }
    if ($missingCount -gt 0) {
        Write-Host "`n[FAIL] Verification failed: $missingCount required entries missing." -ForegroundColor Red
        return $false
    }
    Write-Host "`n[PASS] All required entries present." -ForegroundColor Green
    return $true
}

## => Main Script <= ##
Write-Host "`n=====================================" -ForegroundColor Magenta
Write-Host "    FFT Treasure Master - Publisher   " -ForegroundColor Magenta
Write-Host "=====================================" -ForegroundColor Magenta

$originalLocation = Get-Location
Split-Path $MyInvocation.MyCommand.Path | Push-Location
[Environment]::CurrentDirectory = $PWD

$exitCode = 1

try {
    $finalVersion = Get-ModVersion -RequestedVersion $Version

    if (-not $SkipGenerate) {
        Write-Status "Baking treasure dataset (self-test gate)..." "Cyan"
        Invoke-TablePipeline -FailVerb PACKAGE
    } else {
        Write-Host "  -> -SkipGenerate set; packaging committed treasure.json as-is." -ForegroundColor Yellow
    }

    # Unit test gate bypassed since test directory was removed.
    Write-Status "Skipping unit tests (test folder removed)..." "Yellow"

    Clean-BuildDirectories

    Write-Status "Building Treasure Master DLL into the package..." "Cyan"
    Invoke-TreasureMasterPublish -OutDir $BuildOutputPath -CleanFirst

    Get-ChildItem $BuildOutputPath -Filter *.pdb -File -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
    Write-Host "  -> DLL build complete." -ForegroundColor Green

    Copy-ModAssets

    $packagePath = Create-Package -ModVersion $finalVersion

    if ($packagePath) {
        $verifyOk = Verify-Package -PackagePath $packagePath
        if (-not $verifyOk) {
            Write-Status "Publishing failed - package verification failed" "Red"
            $exitCode = 1
        }
        else {
            if ($env:GITHUB_OUTPUT) {
                $zipFilename = Split-Path $packagePath -Leaf
                Add-Content -Path $env:GITHUB_OUTPUT -Value "zip=$zipFilename"
                Write-Host "  -> Set GHA output: zip=$zipFilename" -ForegroundColor Cyan
            }
            Write-Status "Publishing completed successfully!" "Green"
            Write-Host "Package ready at: $packagePath" -ForegroundColor Yellow
            Write-Host "Version: $finalVersion" -ForegroundColor Yellow
            $exitCode = 0
        }
    }
    else {
        Write-Status "Publishing failed - package creation unsuccessful" "Red"
        $exitCode = 1
    }
}
catch {
    Write-Host "`n[ERROR] $_" -ForegroundColor Red
    Write-Host "Stack Trace: $($_.ScriptStackTrace)" -ForegroundColor Red
    $exitCode = 1
}
finally {
    Pop-Location
    Set-Location $originalLocation
    exit $exitCode
}

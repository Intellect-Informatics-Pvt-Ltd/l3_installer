# ═══════════════════════════════════════════════════════════════════════════════
# publish-win-x64.ps1 — Build self-contained Windows EXEs for installer packaging
# ═══════════════════════════════════════════════════════════════════════════════
#
# Usage:
#   .\scripts\publish-win-x64.ps1                    # publish all (PACS + NLDR)
#   .\scripts\publish-win-x64.ps1 -Group pacs        # publish PACS-side only
#   .\scripts\publish-win-x64.ps1 -Group nldr        # publish NLDR-side only
#   .\scripts\publish-win-x64.ps1 -CreateZip         # also create installer payload ZIPs
#
# Output:
#   publish/pacs/   → Pacs.Fas.Api.exe, Pacs.Loans.Api.exe, Pacs.SyncWorker.exe, Pacs.OperatorUi.exe
#   publish/nldr/   → Nldr.Api.exe, Nldr.SyncWorker.exe, Nldr.DashboardUi.exe
#   publish/        → harness-pacs-win-x64.zip, harness-nldr-win-x64.zip (if -CreateZip)
#
# Prerequisites:
#   - .NET 8 SDK installed
#   - Run from the harness/ directory (or pass -HarnessRoot)
# ═══════════════════════════════════════════════════════════════════════════════

[CmdletBinding()]
param(
    [ValidateSet("all", "pacs", "nldr")]
    [string]$Group = "all",

    [switch]$CreateZip,

    [string]$HarnessRoot = $PSScriptRoot + "\..",

    [string]$OutputDir = "$HarnessRoot\publish",

    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# ─── Project definitions ──────────────────────────────────────────────────────

$PacsProjects = @(
    @{ Name = "Pacs.Fas.Api";     Path = "src\Pacs.Fas.Api\Pacs.Fas.Api.csproj" },
    @{ Name = "Pacs.Loans.Api";   Path = "src\Pacs.Loans.Api\Pacs.Loans.Api.csproj" },
    @{ Name = "Pacs.SyncWorker";  Path = "src\Pacs.SyncWorker\Pacs.SyncWorker.csproj" },
    @{ Name = "Pacs.OperatorUi";  Path = "src\Pacs.OperatorUi\Pacs.OperatorUi.csproj" }
)

$NldrProjects = @(
    @{ Name = "Nldr.Api";          Path = "src\Nldr.Api\Nldr.Api.csproj" },
    @{ Name = "Nldr.SyncWorker";   Path = "src\Nldr.SyncWorker\Nldr.SyncWorker.csproj" },
    @{ Name = "Nldr.DashboardUi";  Path = "src\Nldr.DashboardUi\Nldr.DashboardUi.csproj" }
)

# ─── Helpers ──────────────────────────────────────────────────────────────────

function Publish-Project {
    param(
        [string]$ProjectPath,
        [string]$OutputPath
    )

    $fullPath = Join-Path $HarnessRoot $ProjectPath
    Write-Host "  Publishing: $ProjectPath → $OutputPath" -ForegroundColor Cyan

    dotnet publish $fullPath `
        -c $Configuration `
        -o $OutputPath `
        --no-restore `
        2>&1

    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed for $ProjectPath (exit code $LASTEXITCODE)"
    }
}

function Get-Sha256 {
    param([string]$FilePath)
    $hash = Get-FileHash -Path $FilePath -Algorithm SHA256
    return $hash.Hash.ToLower()
}

# ─── Main ─────────────────────────────────────────────────────────────────────

Write-Host "`n═══ ePACS Harness — win-x64 Self-Contained Publish ═══`n" -ForegroundColor Green

# Restore once
Write-Host "Restoring NuGet packages..." -ForegroundColor Yellow
dotnet restore (Join-Path $HarnessRoot "ePACS.SyncHarness.sln") -r win-x64
if ($LASTEXITCODE -ne 0) { throw "Restore failed" }

# Clean output
if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Path "$OutputDir\pacs" -Force | Out-Null
New-Item -ItemType Directory -Path "$OutputDir\nldr" -Force | Out-Null

# Publish PACS-side
if ($Group -in @("all", "pacs")) {
    Write-Host "`n─── PACS-side services ───" -ForegroundColor Yellow
    foreach ($proj in $PacsProjects) {
        Publish-Project -ProjectPath $proj.Path -OutputPath "$OutputDir\pacs"
    }
    Write-Host "  ✓ PACS services published" -ForegroundColor Green
}

# Publish NLDR-side
if ($Group -in @("all", "nldr")) {
    Write-Host "`n─── NLDR-side services ───" -ForegroundColor Yellow
    foreach ($proj in $NldrProjects) {
        Publish-Project -ProjectPath $proj.Path -OutputPath "$OutputDir\nldr"
    }
    Write-Host "  ✓ NLDR services published" -ForegroundColor Green
}

# Create ZIP payloads for installer
if ($CreateZip) {
    Write-Host "`n─── Creating installer payload ZIPs ───" -ForegroundColor Yellow

    if ($Group -in @("all", "pacs")) {
        $pacsZip = Join-Path $OutputDir "harness-pacs-win-x64.zip"
        Compress-Archive -Path "$OutputDir\pacs\*" -DestinationPath $pacsZip -Force
        $pacsHash = Get-Sha256 $pacsZip
        $pacsSize = (Get-Item $pacsZip).Length
        Write-Host "  PACS ZIP: $pacsZip" -ForegroundColor Cyan
        Write-Host "    SHA-256: $pacsHash" -ForegroundColor DarkGray
        Write-Host "    Size:    $([math]::Round($pacsSize / 1MB, 1)) MB" -ForegroundColor DarkGray
    }

    if ($Group -in @("all", "nldr")) {
        $nldrZip = Join-Path $OutputDir "harness-nldr-win-x64.zip"
        Compress-Archive -Path "$OutputDir\nldr\*" -DestinationPath $nldrZip -Force
        $nldrHash = Get-Sha256 $nldrZip
        $nldrSize = (Get-Item $nldrZip).Length
        Write-Host "  NLDR ZIP: $nldrZip" -ForegroundColor Cyan
        Write-Host "    SHA-256: $nldrHash" -ForegroundColor DarkGray
        Write-Host "    Size:    $([math]::Round($nldrSize / 1MB, 1)) MB" -ForegroundColor DarkGray
    }

    Write-Host "`n  ✓ Payload ZIPs created. Update installer-manifest-stub.yaml with hashes above." -ForegroundColor Green
}

Write-Host "`n═══ Done ═══`n" -ForegroundColor Green

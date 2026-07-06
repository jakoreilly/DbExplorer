<#
.SYNOPSIS
    Builds and runs DbExplorer, then opens it in the default browser.

.DESCRIPTION
    - Stops any DbExplorer instance already holding the HTTPS port.
    - Builds the solution (Release by default; pass -Debug for a Debug build).
    - Launches the app and waits until it is actually listening.
    - Opens the site in the default browser.

    Double-click run.cmd to launch this, or run directly:
        powershell -File run.ps1
        powershell -File run.ps1 -Dev          # Debug build + Development environment
        powershell -File run.ps1 -NoBrowser    # don't auto-open the browser
#>
[CmdletBinding()]
param(
    [switch]$Dev,
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

$project       = Join-Path $root 'DbExplorer'
$configuration = if ($Dev) { 'Debug' } else { 'Release' }
$environment   = if ($Dev) { 'Development' } else { 'Production' }
$httpsPort     = 2027
$url           = "https://localhost:$httpsPort/"

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }

# ── 1. Free the port if a previous instance is still running ──────────────────
Write-Step "Checking for a running instance on port $httpsPort"
try {
    $inUse = Get-NetTCPConnection -LocalPort $httpsPort -State Listen -ErrorAction Stop
    foreach ($conn in $inUse) {
        $pidToKill = $conn.OwningProcess
        $proc = Get-Process -Id $pidToKill -ErrorAction SilentlyContinue
        if ($proc -and ($proc.ProcessName -in @('DbExplorer', 'dotnet'))) {
            Write-Host "    Stopping existing process $($proc.ProcessName) (PID $pidToKill)"
            Stop-Process -Id $pidToKill -Force -Confirm:$false
            Start-Sleep -Milliseconds 800
        }
    }
} catch {
    Write-Host "    Port is free."
}

# ── 2. Build ──────────────────────────────────────────────────────────────────
Write-Step "Building solution ($configuration)"
dotnet build "$root\DbExplorer.sln" -c $configuration -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "`nBuild failed. See errors above." -ForegroundColor Red
    Read-Host "Press Enter to close"
    exit 1
}
Write-Host "    Build succeeded." -ForegroundColor Green

# ── 3. Launch the app in the background ───────────────────────────────────────
Write-Step "Starting DbExplorer ($environment)"
$env:ASPNETCORE_ENVIRONMENT = $environment
$app = Start-Process -FilePath 'dotnet' `
    -ArgumentList @('run', '--project', $project, '-c', $configuration, '--no-build') `
    -PassThru

# ── 4. Wait until the site is actually serving ───────────────────────────────
Write-Step "Waiting for the site to come up at $url"
$ready = $false
for ($i = 0; $i -lt 40; $i++) {
    if ($app.HasExited) {
        Write-Host "`nThe app process exited unexpectedly (exit code $($app.ExitCode))." -ForegroundColor Red
        Read-Host "Press Enter to close"
        exit 1
    }
    if (Test-NetConnection -ComputerName localhost -Port $httpsPort -InformationLevel Quiet -WarningAction SilentlyContinue) {
        $ready = $true
        break
    }
    Start-Sleep -Milliseconds 750
}

if (-not $ready) {
    Write-Host "`nTimed out waiting for the site. It may still be starting." -ForegroundColor Yellow
} else {
    Write-Host "    Site is up." -ForegroundColor Green
    if (-not $NoBrowser) {
        Write-Step "Opening $url"
        Start-Process $url
    }
}

Write-Host "`nDbExplorer is running (PID $($app.Id)) at $url" -ForegroundColor Green
Write-Host "Close this window or press Ctrl+C to stop the app." -ForegroundColor DarkGray

# Keep this window attached to the app so closing the window stops the server.
try {
    Wait-Process -Id $app.Id
} catch {
    # App already stopped.
}

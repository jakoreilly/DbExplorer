<#
.SYNOPSIS
    Force-deletes all bin and obj folders under the directory this script lives in.
.DESCRIPTION
    Recursively finds every folder named "bin" or "obj" at or below the script's
    own location and removes it (with contents). Read-only / hidden files are
    cleared first so the delete cannot be blocked. Safe to drop into any folder
    or subfolder and run.
#>

# Root = the folder this script is placed in (not the caller's working directory).
$root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($root)) {
    $root = Split-Path -Parent $MyInvocation.MyCommand.Definition
}

Write-Host "Scanning for bin/obj folders under:" -ForegroundColor Cyan
Write-Host "  $root`n"

$targets = Get-ChildItem -Path $root -Recurse -Directory -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq 'bin' -or $_.Name -eq 'obj' }

if (-not $targets) {
    Write-Host "Nothing to delete - no bin or obj folders found." -ForegroundColor Green
    return
}

$removed = 0
$failed  = 0

foreach ($dir in $targets) {
    # Skip if a parent bin/obj was already removed in this pass.
    if (-not (Test-Path -LiteralPath $dir.FullName)) { continue }

    try {
        # Clear read-only / hidden / system attributes so nothing blocks the delete.
        Get-ChildItem -LiteralPath $dir.FullName -Recurse -Force -ErrorAction SilentlyContinue |
            ForEach-Object { $_.Attributes = 'Normal' }

        Remove-Item -LiteralPath $dir.FullName -Recurse -Force -ErrorAction Stop
        Write-Host "  deleted  $($dir.FullName)" -ForegroundColor DarkGray
        $removed++
    }
    catch {
        Write-Host "  FAILED   $($dir.FullName)  ->  $($_.Exception.Message)" -ForegroundColor Red
        $failed++
    }
}

Write-Host ""
Write-Host "Done. Removed $removed folder(s)." -ForegroundColor Green
if ($failed -gt 0) {
    Write-Host "$failed folder(s) could not be deleted (in use? close VS / dotnet processes)." -ForegroundColor Yellow
    exit 1
}

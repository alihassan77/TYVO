<#
TYVO setup check (Windows). Safe to run every time: it skips whatever is already in place.

  powershell -NoProfile -ExecutionPolicy Bypass -File bootstrap.ps1 [-Project "<Unity project>"]

1. Finds Python 3.8+; installs Python 3.12 with winget (per user, no admin) only if none is found.
2. With -Project: runs `tyvo.py ensure`, which installs the Unity package only if it is missing or out of date.
The last line printed is `PYTHON=<path>`: use that interpreter for tyvo.py.
#>
param([string]$Project = "")
$ErrorActionPreference = "Stop"

function Find-Python {
    $candidates = @()
    foreach ($name in "python", "python3", "py") {
        Get-Command $name -All -ErrorAction SilentlyContinue | ForEach-Object { $candidates += $_.Source }
    }
    # Default per-user / all-users install folders (PATH may be stale right after an install)
    foreach ($root in "$env:LOCALAPPDATA\Programs\Python", "$env:ProgramFiles\Python*") {
        Get-ChildItem $root -Filter python.exe -Recurse -Depth 1 -ErrorAction SilentlyContinue | ForEach-Object { $candidates += $_.FullName }
    }
    foreach ($exe in $candidates | Select-Object -Unique) {
        # Skip the Microsoft Store alias stubs: they open the Store instead of running Python.
        if ($exe -like "*\WindowsApps\*") { continue }
        try {
            $pyArgs = @("-c", "import sys; print(sys.executable if sys.version_info >= (3, 8) else '')")
            if ((Split-Path $exe -Leaf) -eq "py.exe") { $pyArgs = @("-3") + $pyArgs }
            $out = & $exe @pyArgs 2>$null
            if ($LASTEXITCODE -eq 0 -and $out) { return ($out | Select-Object -Last 1).Trim() }
        } catch { }
    }
    return $null
}

$python = Find-Python
if (-not $python) {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "Python 3 is not installed and winget is not available. Install Python 3 from https://www.python.org/downloads/ and run this again."
    }
    Write-Host "TYVO: Python 3 not found, installing Python 3.12 with winget..."
    winget install -e --id Python.Python.3.12 --scope user --silent --accept-package-agreements --accept-source-agreements | Out-Host
    # Pick up the new PATH entries without restarting the shell
    $env:Path = [Environment]::GetEnvironmentVariable("Path", "User") + ";" + [Environment]::GetEnvironmentVariable("Path", "Machine")
    $python = Find-Python
    if (-not $python) { throw "Python install finished but python.exe was not found. Open a new terminal and run this again." }
    Write-Host "TYVO: installed $python"
} else {
    Write-Host "TYVO: Python already installed: $python"
}

if ($Project) {
    $tyvo = Join-Path $PSScriptRoot "tyvo.py"
    & $python $tyvo --project $Project ensure
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
Write-Output "PYTHON=$python"

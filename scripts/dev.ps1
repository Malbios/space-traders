# SpaceKids local dev against SpaceKids.FakeSpaceTraders (offline / deterministic).
#
# Usage (pwsh, from repo root):
#   pwsh scripts/dev.ps1 fake      # foreground — fake API on http://localhost:5196
#   pwsh scripts/dev.ps1 server    # foreground — app on http://localhost:5290 (needs fake running)
#   pwsh scripts/dev.ps1 stop      # kill anything listening on 5196 / 5290
#   pwsh scripts/dev.ps1 status    # show whether ports are in use

param(
    [Parameter(Position = 0)]
    [ValidateSet("help", "fake", "server", "stop", "status")]
    [string] $Command = "help"
)

$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$FakePort = 5196
$ServerPort = 5290
$FakeBaseUrl = "http://localhost:$FakePort/"
$ServerUrl = "http://localhost:$ServerPort"

function Get-ListenerProcessIds([int[]] $Ports) {
    $ids = @()
    foreach ($port in $Ports) {
        $ids += Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue |
            ForEach-Object { $_.OwningProcess }
    }
    $ids | Select-Object -Unique
}

function Stop-DevServers {
    $pids = Get-ListenerProcessIds @($FakePort, $ServerPort)
    foreach ($pid in $pids) {
        Stop-Process -Id $pid -Force -ErrorAction SilentlyContinue
    }
    if ($pids) {
        Write-Host "Stopped process(es) on port(s) $FakePort / $ServerPort."
    } else {
        Write-Host "No listeners on port(s) $FakePort / $ServerPort."
    }
}

function Show-Status {
    foreach ($port in @($FakePort, $ServerPort)) {
        $pids = Get-ListenerProcessIds @($port)
        if ($pids) {
            Write-Host "Port $port : listening (PID $($pids -join ', '))"
        } else {
            Write-Host "Port $port : free"
        }
    }
}

function Show-Help {
    @"
SpaceKids dev servers (fake SpaceTraders API + local app)

  pwsh scripts/dev.ps1 fake      Fake API at $FakeBaseUrl
  pwsh scripts/dev.ps1 server    App at $ServerUrl (set SpaceTraders__BaseUrl=$FakeBaseUrl)
  pwsh scripts/dev.ps1 stop      Free ports $FakePort and $ServerPort
  pwsh scripts/dev.ps1 status    Show port usage

Token for the fake: FAKE_TOKEN_1
"@
}

switch ($Command) {
    "help" { Show-Help }
    "stop" { Stop-DevServers }
    "status" { Show-Status }
    "fake" {
        Set-Location $RepoRoot
        $env:ASPNETCORE_ENVIRONMENT = "Development"
        Write-Host "Starting FakeSpaceTraders at $FakeBaseUrl"
        dotnet run --project src/SpaceKids.FakeSpaceTraders --urls "http://localhost:$FakePort" --no-launch-profile
    }
    "server" {
        Set-Location $RepoRoot
        $env:ASPNETCORE_ENVIRONMENT = "Development"
        $env:SpaceTraders__BaseUrl = $FakeBaseUrl
        $env:SPACEKIDS_DB_PATH = Join-Path $RepoRoot "src\SpaceKids.Server\spacekids.db"
        $env:SPACEKIDS_BACKUPS_DIR = Join-Path $RepoRoot "src\SpaceKids.Server\backups"
        Write-Host "Starting SpaceKids.Server at $ServerUrl (API -> $FakeBaseUrl)"
        dotnet run --project src/SpaceKids.Server --urls "http://localhost:$ServerPort" --no-launch-profile
    }
}
# E2E Test Environment Setup Script
# Sets up the full Ark development environment and Playwright browsers

param(
    [switch]$Clean,
    [switch]$SkipEnv
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Log {
    param([string]$Message)
    Write-Host "[E2E] $Message" -ForegroundColor Blue
}

function LogError {
    param([string]$Message)
    Write-Host "[E2E ERROR] $Message" -ForegroundColor Red
}

Set-Location $ScriptDir

# 1. Start the Ark development environment (nigiri + ark stack)
if (-not $SkipEnv) {
    Log "Starting Ark development environment..."
    if ($Clean) {
        & bash ./submodules/NNark/NArk.Tests.End2End/Infrastructure/start-env.sh --clean
    } else {
        & bash ./submodules/NNark/NArk.Tests.End2End/Infrastructure/start-env.sh
    }
    if ($LASTEXITCODE -ne 0) {
        LogError "Failed to start environment"
        exit 1
    }
} else {
    Log "Skipping environment setup (--SkipEnv flag set)"
}

# 2. Build the E2E test project
Log "Building E2E test project..."
dotnet build NArk.E2E.Tests/NArk.E2E.Tests.csproj
if ($LASTEXITCODE -ne 0) {
    LogError "Failed to build E2E test project"
    exit 1
}

# 3. Install Playwright browsers
Log "Installing Playwright browsers..."
& pwsh NArk.E2E.Tests/bin/Debug/net8.0/playwright.ps1 install chromium

# 4. Wait for all services to be healthy
Log "Verifying services are healthy..."

# Check Ark daemon
$maxAttempts = 15
$attempt = 1
while ($attempt -le $maxAttempts) {
    try {
        $response = Invoke-WebRequest -Uri "http://localhost:7070/health" -UseBasicParsing -ErrorAction Stop
        Log "Ark daemon is healthy"
        break
    } catch {
        Log "Waiting for Ark daemon... (attempt $attempt/$maxAttempts)"
        Start-Sleep -Seconds 2
        $attempt++
    }
}

if ($attempt -gt $maxAttempts) {
    LogError "Ark daemon failed health check"
    exit 1
}

# Check Boltz API (optional)
try {
    $response = Invoke-WebRequest -Uri "http://localhost:9001/version" -UseBasicParsing -ErrorAction Stop
    Log "Boltz API is healthy"
} catch {
    Log "Warning: Boltz API not responding (some swap tests may fail)"
}

Log ""
Log "========================================"
Log "E2E Test Environment Ready!"
Log "========================================"
Log ""
Log "Run tests with:"
Log "  dotnet test NArk.E2E.Tests"
Log ""
Log "Run with visible browser:"
Log "  `$env:PLAYWRIGHT_HEADLESS='false'; dotnet test NArk.E2E.Tests"
Log ""
Log "Run specific test:"
Log '  dotnet test NArk.E2E.Tests --filter "ClassName.TestMethodName"'
Log ""
Log "Services:"
Log "  Ark daemon:    http://localhost:7070"
Log "  Boltz API:     http://localhost:9001"
Log "  CORS proxy:    http://localhost:9069"
Log "  Chopsticks:    http://localhost:3000"
Log ""

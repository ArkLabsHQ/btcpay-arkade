#!/usr/bin/env bash
set -e

# E2E Test Environment Setup Script
# Sets up the full Ark development environment and Playwright browsers

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

log() {
  local msg="$1"
  local blue="\033[0;34m"
  local reset="\033[0m"
  echo -e "${blue}[E2E] ${msg}${reset}"
}

error() {
  local msg="$1"
  local red="\033[0;31m"
  local reset="\033[0m"
  echo -e "${red}[E2E ERROR] ${msg}${reset}" >&2
}

# Argument parsing
CLEAN=false
SKIP_ENV=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --clean)
      CLEAN=true
      shift
      ;;
    --skip-env)
      SKIP_ENV=true
      shift
      ;;
    *)
      echo "Usage: $0 [--clean] [--skip-env]" >&2
      echo "  --clean     Clean restart with fresh volumes"
      echo "  --skip-env  Skip environment setup (assume already running)"
      exit 1
      ;;
  esac
done

cd "$SCRIPT_DIR"

# 1. Start the Ark development environment (nigiri + ark stack)
if [ "$SKIP_ENV" = false ]; then
  log "Starting Ark development environment..."
  if [ "$CLEAN" = true ]; then
    ./start-env.sh --clean
  else
    ./start-env.sh
  fi
else
  log "Skipping environment setup (--skip-env flag set)"
fi

# 2. Build the E2E test project
log "Building E2E test project..."
dotnet build NArk.E2E.Tests/NArk.E2E.Tests.csproj

# 3. Install Playwright browsers
log "Installing Playwright browsers..."
pwsh NArk.E2E.Tests/bin/Debug/net8.0/playwright.ps1 install chromium

# 4. Wait for all services to be healthy
log "Verifying services are healthy..."

# Check Ark daemon
max_attempts=15
attempt=1
while [ $attempt -le $max_attempts ]; do
  if curl -s http://localhost:7070/health >/dev/null 2>&1; then
    log "Ark daemon is healthy"
    break
  fi
  log "Waiting for Ark daemon... (attempt $attempt/$max_attempts)"
  sleep 2
  ((attempt++))
done

if [ $attempt -gt $max_attempts ]; then
  error "Ark daemon failed health check"
  exit 1
fi

# Check Boltz API (optional - may not be needed for all tests)
if curl -s http://localhost:9001/version >/dev/null 2>&1; then
  log "Boltz API is healthy"
else
  log "Warning: Boltz API not responding (some swap tests may fail)"
fi

log ""
log "========================================"
log "E2E Test Environment Ready!"
log "========================================"
log ""
log "Run tests with:"
log "  dotnet test NArk.E2E.Tests"
log ""
log "Run with visible browser:"
log "  PLAYWRIGHT_HEADLESS=false dotnet test NArk.E2E.Tests"
log ""
log "Run specific test:"
log "  dotnet test NArk.E2E.Tests --filter \"ClassName.TestMethodName\""
log ""
log "Services:"
log "  Ark daemon:    http://localhost:7070"
log "  Boltz API:     http://localhost:9001"
log "  CORS proxy:    http://localhost:9069"
log "  Chopsticks:    http://localhost:3000"
log ""

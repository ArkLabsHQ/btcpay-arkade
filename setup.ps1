# Check if not in a CI environment
if (-not (Test-Path Env:CI)) {
    # Initialize the server submodule
    Write-Host "Initializing and updating submodules..."
    git submodule init
    if ($LASTEXITCODE -eq 0) {
        git submodule update --recursive
    } else {
        Write-Error "git submodule init failed."
        exit 1
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Error "git submodule update --recursive failed."
        exit 1
    }

    # Install the workloads
    Write-Host "Restoring dotnet workloads..."
    dotnet workload restore
    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet workload restore failed."
        exit 1
    }
}

# Create appsettings file to include app plugin when running the server
$appsettings = "submodules/btcpayserver/BTCPayServer/appsettings.dev.json"
if (-not (Test-Path $appsettings -PathType Leaf)) {
    Write-Host "Creating $appsettings..."
    $content = '{ "DEBUG_PLUGINS": "../../../BTCPayServer.Plugins.ArkPayServer/bin/Debug/net8.0/BTCPayServer.Plugins.ArkPayServer.dll" }'
    Set-Content -Path $appsettings -Value $content -Encoding UTF8
}

# Publish each project so dependencies are shared
$root = Get-Location
$pluginDir = "BTCPayServer.Plugins.ArkPayServer"
$publishDir = Join-Path $root "$pluginDir/bin/Debug/net8.0"
$projects = @($pluginDir, "NArk")

# Remove old build artifacts
if (Test-Path $publishDir) {
    Write-Host "Cleaning $publishDir..."
    Remove-Item -Recurse -Force $publishDir
}

function Publish-Project($path) {
    if (-not (Test-Path $path)) {
        Write-Error "Project directory '$path' not found."
        exit 1
    }

    Write-Host "Publishing $path..."
    Push-Location $path
    dotnet publish -c Debug -o $publishDir
    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed for $path."
        Pop-Location
        exit 1
    }
    Pop-Location
}

foreach ($project in $projects) {
    Publish-Project $project
}

Write-Host "Setup complete."

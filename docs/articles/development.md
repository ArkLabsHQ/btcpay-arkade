# Development Guide

This guide covers building the plugin from source, running tests, and contributing.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker](https://docs.docker.com/get-docker/) (for test environment)
- PostgreSQL (bundled with BTCPay / test environment)
- Git (with submodule support)

## Building from Source

```bash
git clone --recurse-submodules https://github.com/ArkLabsHQ/btcpay-arkade.git
cd btcpay-arkade
dotnet build
```

Or use the setup script which also configures the BTCPay plugin path:

```bash
./setup.sh        # Linux/macOS
.\setup.ps1       # Windows
```

## Project Structure

```
btcpay-arkade/
├── BTCPayServer.Plugins.ArkPayServer/   # Main BTCPay plugin
│   ├── Controllers/                     # HTTP controllers
│   ├── Data/                            # EF Core entities & migrations
│   ├── Models/                          # View models
│   ├── Services/                        # Background services
│   ├── Views/                           # Razor views
│   └── PaymentHandler/                  # BTCPay payment method integration
├── NArk.E2E.Tests/                      # End-to-end test suite
├── submodules/
│   ├── btcpayserver/                    # BTCPay Server source (build dependency)
│   └── NNark/                           # .NET Ark SDK (bundled in plugin)
├── docs/                                # Documentation source (DocFX)
├── setup.sh / setup.ps1                 # First-time setup scripts
└── add-migration.sh / .ps1              # EF Core migration helpers
```

## Running Tests

### Unit Tests

```bash
dotnet test
```

### End-to-End Tests

E2E tests require a local regtest environment with arkd, Bitcoin Core, and supporting services:

```bash
# Start the test environment
./start-env.sh

# On Windows (via WSL):
start-test-env.cmd

# Run E2E tests
dotnet test NArk.E2E.Tests/
```

The test environment spins up:

- **nigiri**: Bitcoin Core + Electrs regtest stack
- **arkd**: Arkade server with wallet sidecar
- **Boltz**: Swap backend with LND and Fulmine

> [!IMPORTANT]
> E2E tests must run sequentially (not in parallel) — they share a single arkd instance and concurrent access causes race conditions.

## Adding EF Core Migrations

When you modify any EF Core entity or `DbContext`:

```bash
./add-migration.sh <MigrationName>
# or on Windows:
.\add-migration.ps1 <MigrationName>
```

Migrations are stored in `BTCPayServer.Plugins.ArkPayServer/Data/Migrations/` and applied automatically when the plugin starts.

## Contributing

1. Fork the repository
2. Create a feature branch: `your-name/short-description`
3. Ensure `dotnet build` passes
4. Add or update tests for new payment flows
5. Open a pull request against `master`

For significant changes, open an issue first to discuss the approach.

## Release Process

CI automatically creates a GitHub Release with the changelog body when a version tag is pushed:

```bash
git tag v2.1.0
git push origin v2.1.0
```

## Building Documentation

Documentation is built with [DocFX](https://dotnet.github.io/docfx/):

```bash
dotnet tool install -g docfx    # Install once
docfx docfx.json                # Build docs
docfx docfx.json --serve        # Build and serve locally at http://localhost:8080
```

The documentation is automatically deployed to GitHub Pages on every push to `master`.

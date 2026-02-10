<#
.SYNOPSIS
    Adds a new Entity Framework Core migration for the ArkPayServer plugin.
.PARAMETER MigrationName
    The name for the new migration. This is a required parameter.
.EXAMPLE
    .\add-migration.ps1 "InitialCreate"
#>
param (
    [Parameter(Mandatory=$true, HelpMessage="Please provide a name for the migration.")]
    [string]$MigrationName
)

# Stop the script if any command fails
$ErrorActionPreference = "Stop"

Write-Host "⏳ Running EF Core migration command..."

# Run the EF Core migration command
dotnet ef migrations add $MigrationName `
  --project BTCPayServer.Plugins.ArkPayServer/BTCPayServer.Plugins.ArkPayServer.csproj `
  --context ArkPluginDbContext `
  --output-dir Data/Migrations

Write-Host "✅ Migration '$MigrationName' added successfully."

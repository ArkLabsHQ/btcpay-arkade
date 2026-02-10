#!/bin/bash

# Exit on error
set -e

# Ensure a migration name is provided
if [ -z "$1" ]; then
  echo "❌ Error: Migration name is required."
  echo "Usage: ./add-migration.sh <MigrationName>"
  exit 1
fi

# Read the migration name from the first argument
MIGRATION_NAME="$1"

# Run the EF Core migration command
dotnet ef migrations add "$MIGRATION_NAME" \
  --project BTCPayServer.Plugins.ArkPayServer/BTCPayServer.Plugins.ArkPayServer.csproj \
  --context ArkPluginDbContext \
  --output-dir Data/Migrations

echo "✅ Migration '$MIGRATION_NAME' added successfully."

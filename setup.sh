#!/usr/bin/env bash

ROOT_DIR=$(pwd)
PLUGIN_DIR="BTCPayServer.Plugins.ArkPayServer"
OUTPUT_DIR="$ROOT_DIR/$PLUGIN_DIR/bin/Debug/net8.0"
PROJECTS=("NArk")

# Remove old build artifacts
if [ -d "$OUTPUT_DIR" ]; then
  echo "Cleaning $OUTPUT_DIR"
  rm -rf "$OUTPUT_DIR"
fi

if [ -z "${CI:-}" ]; then
  echo "Initializing and updating submodules..."
  git submodule init
  git submodule update --recursive

  echo "Restoring workloads..."
  dotnet workload restore
fi

APPSETTINGS="submodules/btcpayserver/BTCPayServer/appsettings.dev.json"
if [ ! -f "$APPSETTINGS" ]; then
  echo "Creating $APPSETTINGS"
  echo '{ "DEBUG_PLUGINS": "../../../BTCPayServer.Plugins.ArkPayServer/bin/Debug/net8.0/BTCPayServer.Plugins.ArkPayServer.dll" }' > "$APPSETTINGS"
fi

publish_project() {
  local dir="$1"
  echo "Publishing $dir..."
  dotnet publish "$dir" -c Debug -o "$OUTPUT_DIR"
}

publish_project "$PLUGIN_DIR"
for project in "${PROJECTS[@]}"; do
  publish_project "$project"
done

echo "✅ Setup complete."

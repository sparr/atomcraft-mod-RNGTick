#!/usr/bin/env bash
# Build the mod and stage it as a loadable zip.
#
#   ./build.sh
#
# Release, not Debug. A Debug assembly carries DebuggableAttribute with DisableOptimizations,
# which turns the JIT off for that assembly entirely: nothing is inlined, locals are not
# enregistered, and this mod's postfix measures four times its Release cost. That is what a
# player would run. Override with CONFIG=Debug for a build you intend to attach a debugger to.
set -euo pipefail
cd "$(dirname "$0")"

CONFIG="${CONFIG:-Release}"
GAME_DIR="${GAME_DIR:-$HOME/Games/Steam/steamapps/common/Atomcraft}"

# Restoring with no configured sources at all uses the global packages folder and nothing else:
# instant when the cache is warm, and an immediate, legible error rather than a multi-minute
# stall when it is not.
OFFLINE_CONFIG="$(mktemp)"
trap 'rm -f "$OFFLINE_CONFIG"' EXIT
cat > "$OFFLINE_CONFIG" <<'XML'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
  </packageSources>
</configuration>
XML

nice -n 19 dotnet restore src/RNGTick.csproj --configfile "$OFFLINE_CONFIG" \
    -p:GameInstallDir="$GAME_DIR" -p:Configuration="$CONFIG" >/dev/null
nice -n 19 dotnet build src/RNGTick.csproj --no-restore -v q --nologo -c "$CONFIG" \
    -p:GameInstallDir="$GAME_DIR"

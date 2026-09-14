#!/usr/bin/env bash
# Build both mods, and optionally stage them where the harness's test game will load them.
#
#   ./build.sh [--install]
#
# The harness documents its own build-mod.sh for this, and it supplies the same properties.
# This exists for two reasons. Its restore step falls through to the configured package
# sources, which can stall for many minutes on a machine with poor reach to nuget.org even
# though every package needed is already in the local cache; restoring with no sources at all
# uses the global packages folder and nothing else, which is instant when the cache is warm and
# an immediate, legible error when it is not. And it keeps this project off the harness's
# scripts for building, so a harness checkout being mid-edit cannot break a build here.
set -euo pipefail
cd "$(dirname "$0")"

# Release, not Debug. A Debug assembly carries DebuggableAttribute with DisableOptimizations,
# which turns the JIT off for it entirely: nothing is inlined, locals are not enregistered, and
# RNG.Roll's postfix measured four times its Release cost. That is what players would run.
# Override with CONFIG=Debug for a build you intend to attach a debugger to.
CONFIG="${CONFIG:-Release}"

GAME_DIR="${GAME_DIR:-$HOME/Games/Steam/steamapps/common/Atomcraft}"
# A private test root. The default one is shared with whatever else is being developed
# against this harness, and installing mod zips into it makes one session's failures show up
# in another's run.
TEST_ROOT="${TEST_ROOT:-$HOME/.cache/atomcraft-test-rngtick}"
INSTALL="${INSTALL:-$TEST_ROOT/install}"

INSTALL_IT=0
[ "${1:-}" = "--install" ] && INSTALL_IT=1

ARGS=(-p:GameInstallDir="$GAME_DIR"
      -p:AppData="$TEST_ROOT/scratch-appdata"
      -p:TestHarnessDir="$TEST_ROOT/harness")
[ "$INSTALL_IT" = 1 ] && ARGS+=(-p:TestInstallDir="$INSTALL")

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

for project in src/RNGTick.csproj test/RNGTick.Test.csproj conformance/RNGConformance.csproj; do
    echo "==> $project"
    nice -n 19 dotnet restore "$project" --configfile "$OFFLINE_CONFIG" -p:Configuration="$CONFIG" "${ARGS[@]}" >/dev/null
    nice -n 19 dotnet build "$project" --no-restore -v q --nologo -c "$CONFIG" "${ARGS[@]}"
done

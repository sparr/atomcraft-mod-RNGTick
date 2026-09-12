#!/usr/bin/env bash
# Build both mods and run the suite against a test root private to this project.
#
#   ./run-tests.sh [extra args passed through to the harness]
#
# The harness's own run-tests.sh defaults to a shared test root. Sharing it means one
# project's mod zips and results land where another project is also running, so a failure
# caused here surfaces over there. Everything below stays under $TEST_ROOT.
set -euo pipefail
cd "$(dirname "$0")"


export TEST_ROOT="${TEST_ROOT:-$HOME/.cache/atomcraft-test-rngtick}"
HARNESS="${HARNESS:-../Testing}"

# Provision the private root once, by cloning the shared one's patched game if it exists.
if [ ! -d "$TEST_ROOT/install" ]; then
    SHARED="${SHARED_TEST_ROOT:-$HOME/.cache/atomcraft-test}"
    if [ -d "$SHARED/install" ]; then
        echo "==> seeding $TEST_ROOT from $SHARED"
        mkdir -p "$TEST_ROOT"
        # Hardlinked: the game install is hundreds of megabytes and is already
        # patched, and nothing here rewrites it in place. Only the mod zips differ,
        # and replacing one of those breaks its link rather than the original.
        cp -al "$SHARED/install" "$TEST_ROOT/install" 2>/dev/null \
            || cp -a "$SHARED/install" "$TEST_ROOT/install"
        [ -d "$SHARED/harness" ] && cp -a "$SHARED/harness" "$TEST_ROOT/harness"
        rm -f "$TEST_ROOT/install/Mods/"*.zip
    else
        echo "==> no patched game at $SHARED; running the harness bootstrap"
        "$HARNESS/bootstrap.sh"
    fi
fi

# Build the harness first and publish its assembly where this project compiles against it.
# build-mod.sh does not set TestRoot, so the harness's own ExportForConsumers target does not
# run, and without this step the mods here build against whatever version was seeded into
# $TEST_ROOT/harness when it was created. That produced a wall of CS0117 for API that plainly
# existed, which is a confusing way to learn the DLL is stale.
echo "==> building the harness"
"$HARNESS/build-mod.sh" "$HARNESS/src/TestHarness.csproj" --install >/dev/null
mkdir -p "$TEST_ROOT/harness"
unzip -o -j "$HARNESS/build/TestHarness.zip" 'TestHarness/Atomcraft.TestHarness.dll' \
    -d "$TEST_ROOT/harness" >/dev/null

./build.sh --install
exec "$HARNESS/run-tests.sh" "$@"

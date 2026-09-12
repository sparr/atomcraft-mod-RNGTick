#!/usr/bin/env bash
# Build both mods and run the suite against a test root private to this project.
#
#   ./run-tests.sh                 this mod's tests only. The everyday loop.
#   ./run-tests.sh --all           plus the harness's own suite. Before a commit or a release,
#                                  or whenever this project has changed anything under
#                                  ../Testing, which it has.
#   ./run-tests.sh -- --atomtest-filter=Bias      an explicit filter wins over both
#
# Why the default is narrow: the harness's own suite is 15 session tests that each generate,
# save, and reload a 6144x6144 world, and it accounts for about a hundred of the two minutes a
# full run takes. None of it exercises this mod -- every test here is a region test that never
# starts a session -- so paying for it on every edit buys nothing. It still has to run before
# anything is called done, because this project patches the game underneath it.
#
# The harness's own run-tests.sh defaults to a shared test root. Sharing it means one
# project's mod zips and results land where another project is also running, so a failure
# caused here surfaces over there. Everything below stays under $TEST_ROOT.
set -euo pipefail
cd "$(dirname "$0")"

# This project's own tests: the mod's suite, and the conformance suite that names no mod. The
# harness matches --atomtest-filter as an unanchored regex over the assembly-qualified test name,
# so anchoring keeps it from also selecting a harness test that happens to mention the mod.
MINE='^(RNGTick|RNGConformance)\.'

ARGS=(); ALL=0; HAS_FILTER=0; HAS_SEPARATOR=0
for arg in "$@"; do
    case "$arg" in
        --all)                  ALL=1 ;;
        --atomtest-filter=*)    HAS_FILTER=1; ARGS+=("$arg") ;;
        --)                     HAS_SEPARATOR=1; ARGS+=("$arg") ;;
        *)                      ARGS+=("$arg") ;;
    esac
done

if [ "$ALL" = 0 ] && [ "$HAS_FILTER" = 0 ]; then
    # Everything after the harness's own -- goes to the game, and a second -- would be passed
    # along as a game argument rather than starting a new group, so append into the existing one.
    [ "$HAS_SEPARATOR" = 1 ] || ARGS+=(--)
    ARGS+=("--atomtest-filter=$MINE")
    echo "==> running this mod's tests only; ./run-tests.sh --all for the harness suite too"
fi

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
exec "$HARNESS/run-tests.sh" ${ARGS[@]+"${ARGS[@]}"}

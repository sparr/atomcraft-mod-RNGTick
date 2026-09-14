#!/usr/bin/env bash
# Build this project's mods and run them against a test root private to this project.
#
#   ./run-tests.sh                 this project's tests. The everyday loop.
#   ./run-tests.sh --retirement    instead: the suite that asks whether the game is still broken
#   ./run-tests.sh --all           everything installed in the test root, no filter at all
#   ./run-tests.sh -- --atomtest-filter=Bias      an explicit filter wins over all three
#
# Why the default excludes the retirement suite: those tests assert that the *unmodified* game
# still has each defect this mod exists for, so a failure there is good news and means a part of
# the mod can be retired. That is a different question from whether the mod is correct, and
# mixing the two makes a red suite unreadable -- which is exactly what happened when the
# 2026-09-12 build fixed the power-of-two cases. See test/RetirementTests.cs.
#
# The harness's own run-tests.sh defaults to a shared test root. Sharing it means one project's
# mod zips and results land where another project is also running, so a failure caused here
# surfaces over there. Everything below stays under $TEST_ROOT.
set -euo pipefail
cd "$(dirname "$0")"

# This project's own tests: the mod's suite, and the conformance suite that names no mod. The
# harness matches --atomtest-filter as an unanchored regex over the assembly-qualified test name,
# so anchoring keeps it from also selecting a harness test that happens to mention the mod.
MINE='^(RNGTick|RNGConformance)\.'
RETIREMENT='^RNGTick\.Test\.RetirementTests\.'

ARGS=(); ALL=0; ONLY_RETIREMENT=0; HAS_FILTER=0; HAS_SEPARATOR=0
for arg in "$@"; do
    case "$arg" in
        --all)                  ALL=1 ;;
        --retirement)           ONLY_RETIREMENT=1 ;;
        --atomtest-filter=*)    HAS_FILTER=1; ARGS+=("$arg") ;;
        --)                     HAS_SEPARATOR=1; ARGS+=("$arg") ;;
        *)                      ARGS+=("$arg") ;;
    esac
done

if [ "$HAS_FILTER" = 0 ] && [ "$ALL" = 0 ]; then
    # Everything after the harness's own -- goes to the game, and a second -- would be passed
    # along as a game argument rather than starting a new group, so append into the existing one.
    [ "$HAS_SEPARATOR" = 1 ] || ARGS+=(--)
    if [ "$ONLY_RETIREMENT" = 1 ]; then
        ARGS+=("--atomtest-filter=$RETIREMENT")
        echo "==> asking whether the game is still broken; a failure here means something can be retired"
    else
        ARGS+=("--atomtest-filter=$MINE" "--atomtest-exclude=$RETIREMENT")
        echo "==> running this project's tests; ./run-tests.sh --retirement for the other question"
    fi
fi

export TEST_ROOT="${TEST_ROOT:-$HOME/.cache/atomcraft-test-rngtick}"

# --- where the harness comes from --------------------------------------------------------
#
# Two things, and neither of them is a development checkout. This project does not build the
# harness and does not read its sources: a tree someone is working in can be mid-edit and
# uncompilable, and when that happened it took this project's tests down with it for reasons
# that had nothing to do with this project.
#
#   ATOMCRAFT_HARNESS      the tooling that provisions and launches a patched game copy:
#                          run-tests.sh, bootstrap.sh, lib/. Use a release source archive, or
#                          a checkout parked on a release tag. Nothing here compiles it.
#   ATOMCRAFT_HARNESS_ZIP  a pinned TestHarness.zip. Supplies both the assembly these mods
#                          compile against and the harness mod the game loads, so the two can
#                          never disagree.
#
# Set them in the environment, in ./harness.conf here, or in the harness's own per-user config
# at $XDG_CONFIG_HOME/atomcraft-test/config. The environment wins. There are no path defaults:
# this file used to name a sibling directory, that directory was renamed, and nothing noticed.
_env_harness="${ATOMCRAFT_HARNESS:-}"
_env_zip="${ATOMCRAFT_HARNESS_ZIP:-}"
for candidate in ./harness.conf "${XDG_CONFIG_HOME:-$HOME/.config}/atomcraft-test/config"; do
    [ -f "$candidate" ] || continue
    # shellcheck disable=SC1090
    . "$candidate"
    break
done
[ -n "$_env_harness" ] && ATOMCRAFT_HARNESS="$_env_harness"
[ -n "$_env_zip" ] && ATOMCRAFT_HARNESS_ZIP="$_env_zip"
HARNESS="${ATOMCRAFT_HARNESS:-}"
HARNESS_ZIP="${ATOMCRAFT_HARNESS_ZIP:-}"

if [ -z "$HARNESS" ] || [ -z "$HARNESS_ZIP" ]; then
    cat >&2 <<'EOF'
error: this project does not guess where the TestHarness is, and does not build it. Point it
at a release of https://github.com/sparr/atomcraft-mod-TestHarness:

    ATOMCRAFT_HARNESS=/path/to/testharness-release      # the tooling: run-tests.sh, bootstrap.sh, lib/
    ATOMCRAFT_HARNESS_ZIP=/path/to/TestHarness.zip      # the pinned mod, and the assembly to build against

Put both lines in ./harness.conf, which is gitignored. See harness.conf.example. Do not point
either at a tree you are developing the harness in: a mid-edit checkout will fail this project's
tests for reasons that have nothing to do with this project.
EOF
    exit 1
fi

for script in run-tests.sh bootstrap.sh lib/common.sh; do
    [ -e "$HARNESS/$script" ] || {
        echo "error: ATOMCRAFT_HARNESS=$HARNESS has no $script. It should be an extracted" \
             "release source archive, or a checkout parked on a release tag." >&2
        exit 1
    }
done
[ -f "$HARNESS_ZIP" ] || { echo "error: no file at ATOMCRAFT_HARNESS_ZIP=$HARNESS_ZIP" >&2; exit 1; }

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
        rm -f "$TEST_ROOT/install/Mods/"*.zip
    else
        echo "==> no patched game at $SHARED; running the harness bootstrap"
        "$HARNESS/bootstrap.sh"
    fi
fi

# The assembly these mods compile against, taken out of the same zip the game will load. Doing
# it from the zip rather than from a build output is what makes "compiled against" and "ran
# against" the same bytes by construction rather than by habit.
mkdir -p "$TEST_ROOT/harness"
unzip -o -j "$HARNESS_ZIP" '*/Atomcraft.TestHarness.dll' -d "$TEST_ROOT/harness" >/dev/null
echo "==> harness: $HARNESS_ZIP"

./build.sh --install
exec "$HARNESS/run-tests.sh" --no-build --mod "$HARNESS_ZIP" ${ARGS[@]+"${ARGS[@]}"}

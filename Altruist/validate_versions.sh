#!/bin/bash

set -e

# Get the directory of the script
SCRIPT_DIR=$(dirname "$0")

# Navigate to the directory where the script is located
cd "$SCRIPT_DIR"

VERSION_FILE="version.txt"
EXPECTED_VERSION=$(cat $VERSION_FILE)
ERRORS=0

# The framework, the CLI and the project template ship together under one version.
PROJECTS=$(find . ../CLI ../Templates -name '*.csproj' -not -path '*/bin/*' -not -path '*/obj/*')

echo "Checking versions match version.txt ($EXPECTED_VERSION)..."
echo ""

for csproj in $PROJECTS; do
  VERSIONS=$(grep -oE '<(Package)?Version>[^<]*</(Package)?Version>' "$csproj" | sed -E 's|<(Package)?Version>([^<]*)<.*|\2|' || true)
  # A project created from the template references the Altruist packages of the release it came with.
  case "$csproj" in
    ../Templates/*/content/*)
      VERSIONS="$VERSIONS $(grep -oE 'Include="Altruist[A-Za-z.]*" Version="[^"]*"' "$csproj" | sed -E 's|.*Version="([^"]*)"|\1|')" ;;
  esac

  if [ -z "${VERSIONS// /}" ]; then
    echo "  ⚠️  $csproj => no version (skipped)"
    continue
  fi

  for VERSION in $VERSIONS; do
    if [ "$VERSION" == "$EXPECTED_VERSION" ]; then
      echo "  ✅ $csproj => $VERSION"
    else
      echo "  ❌ $csproj => $VERSION (expected $EXPECTED_VERSION)"
      ERRORS=$((ERRORS + 1))
    fi
  done
done

echo ""

if [ $ERRORS -gt 0 ]; then
  echo "❌ $ERRORS version(s) do not match. Run update_versions.sh to fix."
  exit 1
fi

echo "✅ All project versions match version.txt ($EXPECTED_VERSION)"

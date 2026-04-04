#!/bin/bash

set -e

# Get the directory of the script
SCRIPT_DIR=$(dirname "$0")

# Navigate to the directory where the script is located
cd "$SCRIPT_DIR"

VERSION_FILE="version.txt"
EXPECTED_VERSION=$(cat $VERSION_FILE)
ERRORS=0

echo "Checking .csproj versions match version.txt ($EXPECTED_VERSION)..."
echo ""

for csproj in $(find . -name '*.csproj'); do
  VERSION=$(grep '<Version>' "$csproj" | sed -E 's|.*<Version>(.*)</Version>.*|\1|')

  if [ -z "$VERSION" ]; then
    echo "  ⚠️  $csproj => no <Version> tag (skipped)"
    continue
  fi

  if [ "$VERSION" == "$EXPECTED_VERSION" ]; then
    echo "  ✅ $csproj => $VERSION"
  else
    echo "  ❌ $csproj => $VERSION (expected $EXPECTED_VERSION)"
    ERRORS=$((ERRORS + 1))
  fi
done

echo ""

if [ $ERRORS -gt 0 ]; then
  echo "❌ $ERRORS project(s) have mismatched versions. Run update_versions.sh to fix."
  exit 1
fi

echo "✅ All project versions match version.txt ($EXPECTED_VERSION)"

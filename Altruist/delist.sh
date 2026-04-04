#!/bin/bash
set -e

# Usage: ./delist.sh <PackageId>
# Example: ./delist.sh Altruist.EFCore
#          ./delist.sh Altruist.ScyllaDB
#
# Unlists ALL versions of a NuGet package and marks them deprecated.
# Prompts for API key securely (hidden input).

PACKAGE_ID="$1"

if [ -z "$PACKAGE_ID" ]; then
  echo "Usage: ./delist.sh <PackageId>"
  echo "Example: ./delist.sh Altruist.EFCore"
  exit 1
fi

# Prompt for API key (hidden)
echo -n "NuGet API Key: "
read -s NUGET_API_KEY
echo ""

if [ -z "$NUGET_API_KEY" ]; then
  echo "❌ API key cannot be empty."
  exit 1
fi

echo "📦 Fetching versions for $PACKAGE_ID..."

# Get all versions from NuGet API
VERSIONS=$(curl -s "https://api.nuget.org/v3-flatcontainer/${PACKAGE_ID,,}/index.json" | jq -r '.versions[]' 2>/dev/null)

if [ -z "$VERSIONS" ]; then
  echo "❌ No versions found for $PACKAGE_ID (check package name, case-insensitive)"
  exit 1
fi

VERSION_COUNT=$(echo "$VERSIONS" | wc -l)
echo "Found $VERSION_COUNT version(s):"
echo "$VERSIONS" | sed 's/^/  - /'
echo ""

read -p "⚠️  Unlist ALL $VERSION_COUNT versions of $PACKAGE_ID? (y/N): " CONFIRM
if [ "$CONFIRM" != "y" ] && [ "$CONFIRM" != "Y" ]; then
  echo "Cancelled."
  exit 0
fi

echo ""
FAILED=0
SUCCESS=0

for VERSION in $VERSIONS; do
  echo -n "  Unlisting $PACKAGE_ID $VERSION... "

  RESPONSE=$(curl -s -o /dev/null -w "%{http_code}" \
    -X DELETE \
    "https://www.nuget.org/api/v2/package/$PACKAGE_ID/$VERSION" \
    -H "X-NuGet-ApiKey: $NUGET_API_KEY")

  if [ "$RESPONSE" = "200" ] || [ "$RESPONSE" = "204" ]; then
    echo "✅"
    SUCCESS=$((SUCCESS + 1))
  elif [ "$RESPONSE" = "404" ]; then
    echo "⚠️  already unlisted"
    SUCCESS=$((SUCCESS + 1))
  else
    echo "❌ HTTP $RESPONSE"
    FAILED=$((FAILED + 1))
  fi
done

echo ""
echo "Done: $SUCCESS unlisted, $FAILED failed."
echo ""
echo "📋 To also mark as deprecated (with reason), go to:"
echo "   https://www.nuget.org/packages/$PACKAGE_ID/"
echo "   → Manage Package → Deprecation → Select all versions → Legacy"
echo ""
echo "   (NuGet deprecation API is not available via CLI yet — web UI only)"

#!/bin/bash
# Checks a folder of packed .nupkg files the way a user meets them after the release:
#   1. every Altruist package a package depends on is in the folder, at the version it asks for;
#   2. a project created from the packed template restores and builds against these packages.
# Usage: Tools/check-packages.sh <folder with the .nupkg files>

set -euo pipefail

# On Windows (Git Bash) NuGet needs the native path ("F:/..."), which only `pwd -W` gives.
FEED=$(cd "$1" && (pwd -W 2>/dev/null || pwd))
ERRORS=0

echo "Checking Altruist dependencies of the packages in $FEED..."
for nupkg in "$FEED"/*.nupkg; do
  name=$(basename "$nupkg")
  deps=$(unzip -p "$nupkg" '*.nuspec' | grep -oE '<dependency id="Altruist[^"]*" version="[^"]*"' \
    | sed -E 's|.*id="([^"]*)" version="\[?([^]," ]*).*|\1 \2|' | sort -u || true)
  while read -r id version; do
    [ -z "$id" ] && continue
    if ! ls "$FEED" | grep -qix "$id\.$version\.nupkg"; then
      echo "  ❌ $name depends on $id $version, which is not packed"
      ERRORS=$((ERRORS + 1))
    fi
  done <<< "$deps"
done
if [ $ERRORS -gt 0 ]; then
  echo "❌ $ERRORS dependency(ies) would not resolve for users. Add the missing projects to Altruist/Altruist.sln."
  exit 1
fi
echo "✅ Every Altruist dependency is packed"

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
TEMPLATE=$(ls "$FEED"/Altruist.Templates.*.nupkg)

echo "Creating a project from $(basename "$TEMPLATE")..."
# A hive of its own: the check must not install the template for the user running it.
dotnet new install "$TEMPLATE" --debug:custom-hive "$WORK/hive" > /dev/null
dotnet new altruist -n SmokeServer -o "$WORK/SmokeServer" --debug:custom-hive "$WORK/hive" > /dev/null
cat > "$WORK/SmokeServer/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="packed" value="$FEED" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="packed"><package pattern="Altruist*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
EOF
# The packages restore into a folder of their own, so an older copy in the user's cache cannot stand in for them.
NUGET_PACKAGES="$WORK/packages" dotnet build "$WORK/SmokeServer"
echo "✅ A project created from the template builds against the packed packages"

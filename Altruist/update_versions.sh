#!/bin/bash

set -e

# Get the directory of the script
SCRIPT_DIR=$(dirname "$0")

# Navigate to the directory where the script is located
cd "$SCRIPT_DIR"

VERSION_FILE="version.txt"
NEW_VERSION=$(cat "$VERSION_FILE")

echo "Updating all project versions to: $NEW_VERSION"

# The framework, the CLI and the project template ship together under one version.
for csproj in $(find . ../CLI ../Templates -name '*.csproj' -not -path '*/bin/*' -not -path '*/obj/*'); do
  echo "Updating version in $csproj"
  sed -i.bak -E \
    -e "s|<Version>.*</Version>|<Version>$NEW_VERSION</Version>|" \
    -e "s|<PackageVersion>.*</PackageVersion>|<PackageVersion>$NEW_VERSION</PackageVersion>|" \
    "$csproj"
  rm "${csproj}.bak"
done

# A project created from the template references the Altruist packages of the release it came with.
for csproj in $(find ../Templates -path '*/content/*' -name '*.csproj'); do
  echo "Updating Altruist package references in $csproj"
  sed -i.bak -E "s|(Include=\"Altruist[A-Za-z.]*\" Version=)\"[^\"]*\"|\1\"$NEW_VERSION\"|" "$csproj"
  rm "${csproj}.bak"
done

echo "✅ All project versions updated to $NEW_VERSION"

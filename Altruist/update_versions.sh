#!/bin/bash

set -e

# Get the directory of the script
SCRIPT_DIR=$(dirname "$0")

# Navigate to the directory where the script is located
cd "$SCRIPT_DIR"

VERSION_FILE="version.txt"
NEW_VERSION=$(cat $VERSION_FILE)

echo "Updating all project versions to: $NEW_VERSION"

for csproj in $(find . -name '*.csproj'); do
  echo "Updating version in $csproj"
  sed -i.bak -E "s|<Version>.*</Version>|<Version>$NEW_VERSION</Version>|" "$csproj"
  rm "${csproj}.bak"
done

echo "✅ All project versions updated to $NEW_VERSION"

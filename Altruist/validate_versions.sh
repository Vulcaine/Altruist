#!/bin/bash

set -e

# Get the directory of the script
SCRIPT_DIR=$(dirname "$0")

# Navigate to the directory where the script is located
cd "$SCRIPT_DIR"

VERSION_FILE="version.txt"
EXPECTED_VERSION=$(cat $VERSION_FILE)
FOUND_VERSIONS=()

echo "Checking .csproj versions..."

for csproj in $(find . -name '*.csproj'); do
  VERSION=$(grep '<Version>' "$csproj" | sed -E 's|.*<Version>(.*)</Version>.*|\1|')
  echo "$csproj => $VERSION"
  FOUND_VERSIONS+=("$VERSION")
done

# Get unique versions
UNIQUE_VERSIONS=($(printf "%s\n" "${FOUND_VERSIONS[@]}" | sort -u))

if [ "${#UNIQUE_VERSIONS[@]}" -ne 1 ]; then
  echo "❌ Error: Not all .csproj files have the same version."
  printf "Versions found: %s\n" "${UNIQUE_VERSIONS[@]}"
  exit 1
fi

if [ "${UNIQUE_VERSIONS[0]}" == "$EXPECTED_VERSION" ]; then
  echo "❌ Error: Project version has not been bumped. It still matches version.txt ($EXPECTED_VERSION)."
  exit 1
fi

echo "✅ All project versions are consistent and different from version.txt ($EXPECTED_VERSION)"

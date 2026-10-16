#!/bin/bash

set -euo pipefail

usage() {
    echo "Usage: ./scripts/release.sh <version>"
    echo "Example: ./scripts/release.sh 1.8"
}

VERSION="${1:-}"
if [[ -z "$VERSION" ]]; then
    usage
    exit 1
fi

TAG="v$VERSION"
WINDOWS_CSPROJ="KeyStats.Windows/KeyStats/KeyStats.csproj"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
cd "$PROJECT_DIR"

echo "Release $TAG"

perl -0pi -e "s#<Version>[^<]+</Version>#<Version>${VERSION}</Version>#g" "$WINDOWS_CSPROJ"
echo "Set Windows Version=$VERSION"

echo "Committing version bump..."
git add "$WINDOWS_CSPROJ"
git commit -m "chore: bump version to $VERSION"

echo "Tagging and pushing..."
git tag "$TAG"
git push origin main
git push origin "$TAG"

echo ""
echo "Release complete for $TAG"
echo "GitHub Actions will build and publish the Windows package."

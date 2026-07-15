#!/usr/bin/env bash
# release.sh — Cut a new release of HardenWorkstation
#
# Usage:
#   ./tools/release.sh 1.0.7          # tag & push v1.0.7
#   ./tools/release.sh 1.0.7 dry-run  # preview without pushing
#
# What it does:
#   1. Updates <Version> in src/HardenWorkstation.csproj
#   2. Commits the version bump
#   3. Creates an annotated git tag v<version>
#   4. Pushes the commit and tag to origin
#
# The GitHub Actions workflow (.github/workflows/release.yml) triggers on
# the tag push and handles the rest: build, SHA256, and GitHub Release.

set -euo pipefail

VERSION="${1:?Usage: $0 <version> [dry-run]}"
DRY_RUN="${2:-}"

CSPROJ="src/HardenWorkstation.csproj"
TAG="v${VERSION}"

if [ ! -f "$CSPROJ" ]; then
    echo "ERROR: $CSPROJ not found — run from repo root." >&2
    exit 1
fi

# ── 1. Update version in .csproj ──────────────────────────────────
echo "==> Setting version to ${VERSION} in ${CSPROJ} ..."
if [[ "$OSTYPE" == "darwin"* ]]; then
    sed -i '' "s|<Version>[0-9]\+\.[0-9]\+\.[0-9]\+</Version>|<Version>${VERSION}</Version>|" "$CSPROJ"
else
    sed -i "s|<Version>[0-9]\+\.[0-9]\+\.[0-9]\+</Version>|<Version>${VERSION}</Version>|" "$CSPROJ"
fi

git diff --quiet "$CSPROJ" && echo "WARNING: Version in csproj was already ${VERSION}." || true

# ── 2. Commit version bump ────────────────────────────────────────
echo "==> Committing version bump ..."
git add "$CSPROJ"
git commit -m "chore: bump version to ${VERSION}"

# ── 3. Create annotated tag ───────────────────────────────────────
echo "==> Creating tag ${TAG} ..."
git tag -a "${TAG}" -m "Release ${TAG}"

# ── 4. Push (unless dry-run) ──────────────────────────────────────
if [ "$DRY_RUN" = "dry-run" ]; then
    echo "==> DRY-RUN: would push commit + tag ${TAG} to origin"
    echo "    Run without 'dry-run' to push."
else
    echo "==> Pushing commit and tag ${TAG} to origin ..."
    git push origin HEAD:main
    git push origin "${TAG}"
    echo ""
    echo "DONE. GitHub Actions will now build and publish the release:"
    echo "  https://github.com/Homka13/Hard-Duck/actions"
fi

#!/usr/bin/env bash
# release.sh — Випуск нового релізу HardenWorkstation
#
# Використання:
#   ./tools/release.sh 1.0.7          # створити тег і запушити v1.0.7
#   ./tools/release.sh 1.0.7 dry-run  # перегляд без пушу
#
# Що робить:
#   1. Оновлює <Version> у src/HardenWorkstation.csproj
#   2. Комітить зміну версії
#   3. Створює анотований git-тег v<version>
#   4. Пушить коміт і тег до origin
#
# GitHub Actions workflow (.github/workflows/release.yml) запускається
# на пуш тегу й виконує решту: збірка, SHA256 і GitHub Release.

set -euo pipefail

VERSION="${1:?Використання: $0 <version> [dry-run]}"
DRY_RUN="${2:-}"

CSPROJ="src/HardenWorkstation.csproj"
TAG="v${VERSION}"

if [ ! -f "$CSPROJ" ]; then
    echo "ПОМИЛКА: $CSPROJ не знайдено — запускайте з кореня репозиторію." >&2
    exit 1
fi

# ── 1. Оновлення версії у .csproj ─────────────────────────────────
echo "==> Встановлення версії ${VERSION} у ${CSPROJ} ..."
if [[ "$OSTYPE" == "darwin"* ]]; then
    sed -i '' "s|<Version>[0-9]\+\.[0-9]\+\.[0-9]\+</Version>|<Version>${VERSION}</Version>|" "$CSPROJ"
else
    sed -i "s|<Version>[0-9]\+\.[0-9]\+\.[0-9]\+</Version>|<Version>${VERSION}</Version>|" "$CSPROJ"
fi

git diff --quiet "$CSPROJ" && echo "УВАГА: Версія у csproj вже була ${VERSION}." || true

# ── 2. Коміт зміни версії ─────────────────────────────────────────
echo "==> Коміт зміни версії ..."
git add "$CSPROJ"
git commit -m "chore: bump version to ${VERSION}"

# ── 3. Створення анотованого тега ──────────────────────────────────
echo "==> Створення тега ${TAG} ..."
git tag -a "${TAG}" -m "Release ${TAG}"

# ── 4. Пуш (крім dry-run) ─────────────────────────────────────────
if [ "$DRY_RUN" = "dry-run" ]; then
    echo "==> DRY-RUN: було б запушено коміт + тег ${TAG} до origin"
    echo "    Запустіть без 'dry-run' для справжнього пушу."
else
    echo "==> Пуш коміту й тега ${TAG} до origin ..."
    git push origin HEAD:main
    git push origin "${TAG}"
    echo ""
    echo "ГОТОВО. GitHub Actions тепер збере й опублікує реліз:"
    echo "  https://github.com/Homka13/Hard-Duck/actions"
fi

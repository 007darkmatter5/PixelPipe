#!/usr/bin/env bash
# Download libretro cores from the official buildbot into ./cores.
# Usage: scripts/fetch-cores.sh [core_name ...]
# Default cores: fceumm snes9x genesis_plus_gx
# Override arch with PIXELPIPE_CORE_ARCH=x86_64 or arm64.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEST="$ROOT/cores"
ARCH="${PIXELPIPE_CORE_ARCH:-}"
if [[ -z "$ARCH" ]]; then
  case "$(uname -m)" in
    x86_64) ARCH=x86_64 ;;
    aarch64 | arm64) ARCH=arm64 ;;
    *)
      echo "unsupported architecture $(uname -m); set PIXELPIPE_CORE_ARCH" >&2
      exit 1
      ;;
  esac
fi

mkdir -p "$DEST"
if [[ $# -eq 0 ]]; then
  set -- fceumm snes9x genesis_plus_gx
fi

for core in "$@"; do
  url="https://buildbot.libretro.com/nightly/linux/${ARCH}/latest/${core}_libretro.so.zip"
  echo "fetch $url"
  tmp="$(mktemp)"
  curl -fsSL -o "$tmp" "$url"
  unzip -o "$tmp" -d "$DEST"
  rm -f "$tmp"
done

ls -lh "$DEST"/*_libretro.so

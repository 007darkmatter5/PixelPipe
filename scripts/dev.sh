#!/usr/bin/env bash
# Run the engine and the Blazor UI together for local development.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

if ! compgen -G "$ROOT/cores/*_libretro.so" >/dev/null; then
  "$ROOT/scripts/fetch-cores.sh"
fi
mkdir -p "$ROOT/data/saves" "$ROOT/data/system"

if [[ ! -x "$ROOT/engine/target/debug/pixelpipe-engine" && ! -x "$ROOT/engine/target/release/pixelpipe-engine" ]]; then
  cargo build --manifest-path "$ROOT/engine/Cargo.toml"
fi

ENGINE="$ROOT/engine/target/debug/pixelpipe-engine"
if [[ "${PIXELPIPE_RELEASE:-}" == "1" ]]; then
  ENGINE="$ROOT/engine/target/release/pixelpipe-engine"
fi

export PIXELPIPE_ROMS="${PIXELPIPE_ROMS:-$ROOT/roms}"
export PIXELPIPE_CORES="${PIXELPIPE_CORES:-$ROOT/cores}"
export PIXELPIPE_DATA="${PIXELPIPE_DATA:-$ROOT/data}"
export PIXELPIPE_BIND="${PIXELPIPE_BIND:-0.0.0.0:9090}"
export PixelPipe__RomsPath="${PixelPipe__RomsPath:-$ROOT/roms}"
export PixelPipe__EngineUrl="${PixelPipe__EngineUrl:-http://127.0.0.1:9090}"
export PATH="${HOME}/.dotnet:${PATH}"
if [[ -d "${HOME}/.dotnet" ]]; then
  export DOTNET_ROOT="${HOME}/.dotnet"
fi

"$ENGINE" &
engine_pid=$!
cleanup() {
  kill "$engine_pid" 2>/dev/null || true
  wait "$engine_pid" 2>/dev/null || true
}
trap cleanup EXIT INT TERM

dotnet run --project "$ROOT/ui/PixelPipe.Web/PixelPipe.Web.csproj" --urls "http://0.0.0.0:8080"

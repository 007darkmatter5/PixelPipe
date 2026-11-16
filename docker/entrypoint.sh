#!/bin/bash
set -euo pipefail
mkdir -p /data/saves /data/system /roms
pixelpipe-engine &
engine_pid=$!
cleanup() {
  kill "$engine_pid" 2>/dev/null || true
  wait "$engine_pid" 2>/dev/null || true
}
trap cleanup EXIT INT TERM
cd /opt/pixelpipe/ui
dotnet PixelPipe.Web.dll &
ui_pid=$!
wait -n "$engine_pid" "$ui_pid"
status=$?
kill "$engine_pid" "$ui_pid" 2>/dev/null || true
wait || true
exit "$status"

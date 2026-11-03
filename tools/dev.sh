#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_HOME="$PWD/.dotnet-home"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export NUGET_PACKAGES="$PWD/.packages"

godot_bin="${OPENRTS_GODOT:-}"
if [[ -z "$godot_bin" ]]; then
  if command -v godot >/dev/null 2>&1; then godot_bin="$(command -v godot)"
  else godot_bin="$PWD/../Godot_mono.app/Contents/MacOS/Godot"; fi
fi

build() {
  if [[ -n "${OPENRTS_NUGET_SOURCE:-}" ]]; then
    dotnet restore OpenRTS.sln --disable-build-servers --source "$OPENRTS_NUGET_SOURCE"
  else
    dotnet restore OpenRTS.sln --disable-build-servers
  fi
  dotnet build OpenRTS.sln --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
}

action="${1:-build}"
if [[ $# -gt 0 ]]; then shift; fi
case "$action" in
  build) build ;;
  test)
    build
    dotnet run --project tests/DeclarativeContent.Tests --no-build --no-launch-profile
    dotnet run --project tests/OpenRTS.Tests --no-build --no-launch-profile
    ;;
  headless) build; dotnet run --project tools/OpenRTS.Headless --no-build --no-launch-profile -- "$@" ;;
  run) build; exec "$godot_bin" --path "$PWD/sandbox" -- "$@" ;;
  editor) build; exec "$godot_bin" --path "$PWD/sandbox" --editor ;;
  verify) "$0" test; python3 tools/verify.py "$godot_bin" ;;
  *) echo 'Usage: tools/dev.sh [build|test|headless|run|editor|verify]' >&2; exit 2 ;;
esac

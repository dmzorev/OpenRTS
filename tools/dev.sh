#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_HOME="$PWD/.dotnet-home"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export NUGET_PACKAGES="$PWD/.packages"

resolve_godot() {
  local name candidate
  godot_bin="${OPENRTS_GODOT:-}"
  if [[ -z "$godot_bin" ]]; then
    for name in godot-mono godot4-mono godot godot4; do
      if godot_bin="$(type -P "$name")"; then break; fi
    done
    if [[ -z "$godot_bin" && "$(uname -s)" == Darwin ]]; then
      for candidate in "$PWD/../Godot_mono.app" /Applications/Godot_mono.app "${HOME:-}/Applications/Godot_mono.app" \
                       /Applications/Godot.app "${HOME:-}/Applications/Godot.app"; do
        if [[ -x "$candidate/Contents/MacOS/Godot" ]]; then
          godot_bin="$candidate/Contents/MacOS/Godot"
          break
        fi
      done
    fi
  elif [[ "$godot_bin" != */* && ! -x "$godot_bin" ]]; then
    godot_bin="$(type -P "$godot_bin")" || godot_bin=""
  fi
  if [[ ! -f "$godot_bin" || ! -x "$godot_bin" ]]; then
    echo 'Godot .NET executable not found. Set OPENRTS_GODOT to its executable path.' >&2
    exit 1
  fi
  if [[ "$godot_bin" != /* ]]; then godot_bin="$PWD/$godot_bin"; fi
}

build() {
  local target="${1:-OpenRTS.sln}"
  local configuration="${2:-Debug}"
  if [[ -n "${OPENRTS_NUGET_SOURCE:-}" ]]; then
    dotnet restore "$target" --disable-build-servers -m:1 --source "$OPENRTS_NUGET_SOURCE"
  else
    dotnet restore "$target" --disable-build-servers -m:1
  fi
  dotnet build "$target" --configuration "$configuration" --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
}

import_assets() {
  "$godot_bin" --headless --editor --path "$PWD/${1:-sandbox}" --import
}

run_tests() {
  build
  dotnet run --project tests/OpenRTS.Network.Tests --no-build --no-launch-profile
  dotnet run --project tests/DeclarativeContent.Tests --no-build --no-launch-profile
  dotnet run --project tests/OpenRTS.Tests --no-build --no-launch-profile -- "$@"
}

action="${1:-build}"
if [[ $# -gt 0 ]]; then shift; fi
case "$action" in
  run|editor|run3d|editor3d|verify3d|map-build|scene-map|scene-map-pack|verify-map-export|verify-authoring|verify-directory|verify-replay|verify-menu|verify-network|verify)
    resolve_godot ;;
esac

case "$action" in
  build) build ;;
  test) run_tests "$@" ;;
  match-directory) build tools/OpenRTS.MatchDirectory/OpenRTS.MatchDirectory.csproj; exec dotnet run --project tools/OpenRTS.MatchDirectory --no-build --no-launch-profile -- "$@" ;;
  headless) build; dotnet run --project tools/OpenRTS.Headless --no-build --no-launch-profile -- "$@" ;;
  replay) build tools/OpenRTS.Replay/OpenRTS.Replay.csproj; exec dotnet run --project tools/OpenRTS.Replay --no-build --no-launch-profile -- "$@" ;;
  benchmark)
    build tools/OpenRTS.Headless/OpenRTS.Headless.csproj Release
    DOTNET_TieredCompilation=0 dotnet run --project tools/OpenRTS.Headless --configuration Release --no-build --no-launch-profile -- --benchmark "$@"
    ;;
  run) build; import_assets; exec "$godot_bin" --path "$PWD/sandbox" -- "$@" ;;
  editor) build; exec "$godot_bin" --path "$PWD/sandbox" --editor ;;
  run3d|editor3d|verify3d)
    build sandbox3d/OpenRTS.Sandbox3D.csproj
    if [[ "$action" == editor3d ]]; then
      exec "$godot_bin" --path "$PWD/sandbox3d" --editor "$@"
    fi
    import_assets sandbox3d
    if [[ "$action" == verify3d ]]; then
      verification_output="$("$godot_bin" --headless --max-fps 60 --quit-after 600 --path "$PWD/sandbox3d" -- --verify)"
      printf '%s\n' "$verification_output"
      if ! grep -q '^Sandbox3D verified:' <<< "$verification_output"; then
        echo 'Sandbox3D did not report a completed verification.' >&2
        exit 1
      fi
      exit 0
    fi
    exec "$godot_bin" --path "$PWD/sandbox3d" -- "$@"
    ;;
  map-build|scene-map|scene-map-pack|verify-map-export|verify-authoring)
    exec python3 tools/dev_maps.py "$godot_bin" "$action" "$@"
    ;;
  verify-wan)
    build
    if [[ "${1:-}" == "--netem" ]]; then
      shift
      docker build -t openrts-netem:net2 -f tools/network/Dockerfile tools/network
      python3 tools/verify_wan_netem.py "$@"
    else
      dotnet run --project tests/OpenRTS.Network.Tests --no-build --no-launch-profile -- --wan "$@"
    fi
    ;;
  verify-directory) build; import_assets; python3 tools/verify_directory.py "$godot_bin" "$@" ;;
  verify-replay) build; import_assets; python3 tools/verify_replay.py "$godot_bin" "$@" ;;
  verify-menu)
    build; import_assets
    if [[ "${1:-}" == "--visible" ]]; then
      shift; exec "$godot_bin" --path "$PWD/sandbox" -- --verify-menu "$@"
    else
      exec "$godot_bin" --headless --path "$PWD/sandbox" -- --verify-menu "$@"
    fi
    ;;
  verify-network) build; import_assets; python3 tools/verify_network.py "$godot_bin" "$@" ;;
  verify) run_tests; import_assets; python3 tools/verify.py "$godot_bin" ;;
  *) echo 'Usage: tools/dev.sh [build|test|headless|replay|benchmark|run|editor|run3d|editor3d|verify3d|map-build|scene-map|scene-map-pack|verify|verify-authoring|verify-map-export|verify-network|verify-menu|verify-replay|verify-wan|match-directory|verify-directory]' >&2; exit 2 ;;
esac

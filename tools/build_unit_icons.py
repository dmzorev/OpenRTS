#!/usr/bin/env python3
"""Render composed model icons through the game renderer using Godot .NET."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
SANDBOX = ROOT / "sandbox"


def find_godot(value: str) -> str | None:
    if value:
        resolved = shutil.which(value)
        if resolved:
            return resolved
        candidate = Path(value).expanduser()
        if candidate.is_file():
            return str(candidate.resolve())
    for name in ("godot-mono", "godot4-mono", "godot", "godot4"):
        resolved = shutil.which(name)
        if resolved:
            return resolved
    if sys.platform == "darwin":
        for candidate in (ROOT.parent / "Godot_mono.app", Path("/Applications/Godot_mono.app"),
                          Path.home() / "Applications/Godot_mono.app", Path("/Applications/Godot.app")):
            executable = candidate / "Contents/MacOS/Godot"
            if executable.is_file():
                return str(executable)
    return None

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("game", type=Path, help="Path to game.yaml or isometric-game.yaml")
parser.add_argument("--id", action="append", default=[], help="Unit or model ID, repeat for multiple IDs")
parser.add_argument("--vehicles", action="store_true", help="Only movable light/mechanical units, including aircraft")
parser.add_argument("--size", type=int, default=256)
parser.add_argument("--output", type=Path, help="Output directory inside sandbox")
parser.add_argument("--install", action="store_true", help="Reference the generated icon declarations from the game manifest")
parser.add_argument("--godot", default=os.environ.get("OPENRTS_GODOT", "godot"))
args = parser.parse_args()
game = args.game.resolve()
output = (args.output or SANDBOX / "assets" / "icons" / game.stem).resolve()
try:
    relative = output.relative_to(SANDBOX)
except ValueError:
    parser.error("--output must be inside " + str(SANDBOX))
if not 32 <= args.size <= 2048:
    parser.error("--size must be 32..2048")
godot = find_godot(args.godot)
if not godot:
    parser.error("Godot .NET executable not found, pass --godot or OPENRTS_GODOT")

environment = os.environ.copy()
environment.setdefault("DOTNET_CLI_HOME", str(ROOT / ".dotnet-home"))
environment.setdefault("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
environment.setdefault("DOTNET_NOLOGO", "1")
environment.setdefault("NUGET_PACKAGES", str(ROOT / ".packages"))
subprocess.run(["dotnet", "build", str(SANDBOX / "OpenRTS.Sandbox.csproj"), "-m:1",
                "-p:UseSharedCompilation=false"], cwd=ROOT, check=True, env=environment)
command = [godot, "--log-file", str(Path(tempfile.gettempdir()) / "openrts-unit-icons-godot.log"),
           "--path", str(SANDBOX), "--rendering-method", "gl_compatibility",
           # Headless Godot only provides the dummy renderer, so keep a tiny window for offscreen captures.
           "--resolution", "1x1", "--position", "0,0",
           "--scene", "res://IconBuilder.tscn", "--",
           "--game=" + str(game), "--output=" + str(output),
           "--resource-dir=res://" + relative.as_posix(), "--size=" + str(args.size),
           "--ids=" + ",".join(args.id)]
if args.vehicles:
    command.append("--vehicles")
subprocess.run(command, cwd=ROOT, check=True, env=environment)


def install(declaration: str) -> None:
    lines = game.read_text().splitlines()

    def bounds(key: str) -> tuple[int, int] | None:
        for index, line in enumerate(lines):
            if line.strip() != key + ":":
                continue
            if line != key + ":":
                parser.error(f"{game}: declare {key} as a block list to install generated files")
            end = index + 1
            while end < len(lines) and (lines[end].startswith((" ", "\t")) or not lines[end].strip()):
                end += 1
            return index + 1, end
        return None

    item = "- " + declaration
    # The first revision of the tool declared its generated file through Models.
    stale = {item, "- " + (Path(declaration).with_name("models.yaml")).as_posix()}
    models = bounds("Models")
    if models is not None:
        start, end = models
        lines[start:end] = [line for line in lines[start:end] if line.strip() not in stale]
    current = bounds("Icons")
    if current is None:
        lines.extend(["Icons:", "  " + item])
    else:
        start, end = current
        if item not in (line.strip() for line in lines[start:end]):
            lines.insert(end, "  " + item)
    game.write_text("\n".join(lines) + "\n")


declaration = os.path.relpath(output / "icons.yaml", game.parent).replace(os.sep, "/")
if args.install:
    install(declaration)
# Texture loading at runtime requires Godot import metadata for the new PNGs.
subprocess.run([godot, "--headless", "--editor", "--path", str(SANDBOX), "--import"],
               cwd=ROOT, check=True, env=environment)
print(output)
if args.install:
    print("Installed " + declaration + " in " + str(game))

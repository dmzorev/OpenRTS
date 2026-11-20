#!/usr/bin/env python3
"""Record a real TLS match, then exercise its replay through native Godot controls."""
import argparse
import json
from pathlib import Path
import subprocess
import sys
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument('godot')
parser.add_argument('--visible', action='store_true')
parser.add_argument('--output', type=Path)
parser.add_argument('--map', help='Use a compiled skirmish map or scene-map bundle')
args = parser.parse_args()
root = Path(__file__).resolve().parent.parent
output = (args.output or Path(tempfile.mkdtemp(prefix='openrts-replay-'))).resolve()
output.mkdir(parents=True, exist_ok=True)
with (output / 'record.log').open('w') as log:
    network = [sys.executable, str(root / 'tools/verify_network.py'), args.godot, '--host-fog-hold=7', '--output=' + str(output / 'match')]
    if args.map:
        network += ['--map=' + args.map]
    subprocess.run(network,
                   cwd=root, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=90)
host = (output / 'match/host.log').read_text(encoding='utf-8')
paths = [line.split(':', 1)[1] for line in host.splitlines() if line.startswith('OPENRTS_NETWORK_JOURNAL:')]
if len(paths) != 1:
    raise RuntimeError('Recorded match did not publish its journal path')
command = [args.godot]
if not args.visible:
    command += ['--headless']
else:
    command += ['--resolution', '1280x800']
command += ['--path', str(root / 'sandbox'), '--', '--verify-replay=' + paths[0]]
if args.map:
    command += ['--replay-map=' + args.map]
if args.visible:
    command += ['--capture=' + str(output / 'replay.png')]
with (output / 'replay.log').open('w') as log:
    result = subprocess.run(command, cwd=root, stdout=log, stderr=subprocess.STDOUT, timeout=45)
text = (output / 'replay.log').read_text(encoding='utf-8')
if result.returncode or 'OPENRTS_REPLAY_PASS:' not in text:
    raise RuntimeError('Godot replay failed:\n' + text)
expected = next(line.split(':', 1)[1] for line in host.splitlines() if line.startswith('OPENRTS_NETWORK_PASS:'))
if f'OPENRTS_REPLAY_PASS:{expected}' not in text:
    raise RuntimeError('Godot final replay hash differs from the network match')
headless = ['dotnet', 'run', '--project', 'tools/OpenRTS.Replay', '--no-build', '--no-launch-profile', '--', paths[0]]
if args.map:
    headless += ['--map=' + args.map]
result = subprocess.run(headless, cwd=root, capture_output=True, text=True, check=True, timeout=30)
report = next(json.loads(line.split(':', 1)[1]) for line in result.stdout.splitlines() if line.startswith('OPENRTS_REPLAY:'))
if report['StateHash'] != expected:
    raise RuntimeError('Headless final replay hash differs from the network match')
print('PASS Godot replay: embedded recipe, pause/step/speed, seek both directions, player POV, read-only commands, final hash')
print('PASS headless replay:', report)
print('Artifacts:', output)

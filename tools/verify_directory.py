#!/usr/bin/env python3
"""Exercise a real optional HTTP directory and native Godot catalogue and host admission."""
import argparse
from pathlib import Path
import socket
import subprocess
import tempfile
import time
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('godot')
parser.add_argument('--visible', action='store_true')
parser.add_argument('--output', type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parent.parent
output = (args.output or Path(tempfile.mkdtemp(prefix='openrts-directory-'))).resolve()
output.mkdir(parents=True, exist_ok=True)

def port():
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0))
        return probe.getsockname()[1]

url = f'http://127.0.0.1:{port()}'
match_port = port()
processes = []
logs = []
try:
    log = (output / 'directory.log').open('w'); logs.append(log)
    processes.append(subprocess.Popen(['dotnet', str(root / 'tools/OpenRTS.MatchDirectory/bin/Debug/net10.0/OpenRTS.MatchDirectory.dll'),
                                       '--urls=' + url, '--signing-key=' + str(output / 'key.pem')], cwd=root, stdout=log, stderr=subprocess.STDOUT))
    deadline = time.monotonic() + 10
    while True:
        try:
            with urllib.request.urlopen(url + '/v1/info', timeout=.5) as response:
                if response.status == 200: break
        except OSError:
            if time.monotonic() > deadline: raise TimeoutError('Directory did not start')
            time.sleep(.1)
    for role in ('host', 'join'):
        command = [args.godot]
        if not args.visible: command += ['--headless']
        else: command += ['--resolution', '1280x800']
        command += ['--path', str(root / 'sandbox'), '--', '--verify-network=' + role,
                    '--directory-test=' + url, '--network-port=' + str(match_port)]
        if args.visible: command += ['--capture=' + str(output / (role + '.png'))]
        log = (output / (role + '.log')).open('w'); logs.append(log)
        processes.append(subprocess.Popen(command, cwd=root, stdout=log, stderr=subprocess.STDOUT))
    for process in processes[1:]: process.wait(timeout=55)
    hashes = []
    for role, process in zip(('host', 'join'), processes[1:]):
        text = (output / (role + '.log')).read_text(encoding='utf-8')
        if process.returncode or 'OPENRTS_NETWORK_PASS:' not in text: raise RuntimeError(role + ' failed:\n' + text)
        if role == 'join' and 'OPENRTS_DIRECTORY_UI_JOIN:' not in text: raise RuntimeError('Guest bypassed directory controls')
        if role == 'join' and 'OPENRTS_DIRECTORY_CAPACITY:2/8 disabled' not in text: raise RuntimeError('Unsupported capacities were not checked in the browser')
        hashes += [line.split(':', 1)[1] for line in text.splitlines() if line.startswith('OPENRTS_NETWORK_PASS:')]
    if len(hashes) != 2 or len(set(hashes)) != 1: raise RuntimeError('Directory peers disagree')
    print('PASS Godot NET5: publish, account session, catalogue UI, host-assigned seat, TLS match, identical hashes and replay')
    print('Artifacts:', output)
finally:
    for process in processes:
        if process.poll() is None: process.kill(); process.wait()
    for log in logs: log.close()

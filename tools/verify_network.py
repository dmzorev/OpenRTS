#!/usr/bin/env python3
"""Run real Godot clients through lobby, TLS, local menus, production and replay."""
import argparse
from pathlib import Path
import socket
import select
import subprocess
import tempfile
import threading
import time

parser = argparse.ArgumentParser()
parser.add_argument('godot')
parser.add_argument('--lan-address', help='Enter the host IPv4 address, or use auto for LAN discovery, through the locked-room browser')
parser.add_argument('--visible', action='store_true')
parser.add_argument('--host-fog-hold', type=int, default=0)
parser.add_argument('--spectator', action='store_true', help='Add a spectator to the NET3 match')
parser.add_argument('--net3', action='store_true', help='Three human clients plus a host-controlled AI on Crossroads')
parser.add_argument('--reconnect', action='store_true', help='Break guest transport and return through the real in-game button')
parser.add_argument('--slow-client-ms', type=int, default=0, help='Delay one client UI verification while its network owner keeps running')
parser.add_argument('--output', type=Path)
parser.add_argument('--map', help='Use a compiled skirmish map or scene-map bundle for the match')
args = parser.parse_args()
if not 0 <= args.slow_client_ms <= 10000: parser.error('--slow-client-ms must be 0..10000')
if args.lan_address and (args.net3 or args.reconnect or args.spectator): parser.error('--lan-address uses the two-player scenario')
if args.map and args.net3: parser.error('--map uses the two-player scenario')
root = Path(__file__).resolve().parent.parent
output = (args.output or Path(tempfile.mkdtemp(prefix='openrts-network-'))).resolve()
output.mkdir(parents=True, exist_ok=True)
pin = output / 'host.pin'
pin.unlink(missing_ok=True)
with socket.socket() as probe:
    probe.bind(('127.0.0.1', 0))
    port = probe.getsockname()[1]
processes = []
logs = []
relay = None
if args.reconnect:
    if args.net3 or args.spectator: parser.error('--reconnect uses the two-player scenario')
    relay = socket.socket(); relay.bind(('127.0.0.1', 0)); relay.listen(2); relay.settimeout(.2)
    relay_port = relay.getsockname()[1]
    relay_stop = threading.Event()
    relay_connections = []
    relay_lock = threading.Lock()

    def forward(client):
        upstream = None
        try:
            upstream = socket.create_connection(('127.0.0.1', port), timeout=5)
            with relay_lock: relay_connections.extend((client, upstream))
            while not relay_stop.is_set():
                ready, _, _ = select.select((client, upstream), (), (), .2)
                for source in ready:
                    data = source.recv(65536)
                    if not data: return
                    (upstream if source is client else client).sendall(data)
        except OSError:
            pass
        finally:
            with relay_lock:
                for connection in (client, upstream):
                    if connection in relay_connections: relay_connections.remove(connection)
                    if connection is not None: connection.close()

    def accept():
        while not relay_stop.is_set():
            try: client, _ = relay.accept()
            except socket.timeout: continue
            except OSError: return
            threading.Thread(target=forward, args=(client,), daemon=True).start()

    threading.Thread(target=accept, daemon=True).start()
roles = ('host', 'join', 'join3') if args.net3 else ('host', 'join')
if args.spectator:
    if not args.net3: parser.error('--spectator requires --net3')
    roles += ('watch',)
try:
    for role in roles:
        if role != 'host' and not args.lan_address:
            deadline = time.monotonic() + 20
            while (not pin.exists() or len(pin.read_text().strip()) != 64) and time.monotonic() < deadline:
                if processes[0].poll() is not None:
                    raise RuntimeError('Host exited before publishing fingerprint')
                time.sleep(0.1)
            if not pin.exists() or len(pin.read_text().strip()) != 64:
                raise TimeoutError('Host did not publish fingerprint')
        command = [args.godot]
        if args.visible:
            command += ['--position', '20,20' if role == 'host' else '700,20', '--resolution', '900x700']
        else:
            command += ['--headless']
        command += ['--path', str(root / 'sandbox'), '--', '--verify-network=' + role,
                    '--network-port=' + str(relay_port if args.reconnect and role == 'join' else port), '--network-pin-file=' + str(pin)]
        if args.lan_address: command += ['--lan-test=' + args.lan_address]
        if args.map: command += ['--network-map=' + args.map]
        if args.reconnect: command += ['--network-reconnect']
        if args.spectator: command += ['--network-spectators=1']
        if args.slow_client_ms and role in (('join3' if args.net3 else 'join'), 'watch'):
            command += ['--network-ui-delay-ms=' + str(args.slow_client_ms)]
        if role == 'host' and args.host_fog_hold:
            command += ['--fog-hold=' + str(args.host_fog_hold)]
        if args.net3:
            command += ['--network-net3']
        if args.visible:
            command += ['--capture=' + str(output / (role + '.png'))]
        log = (output / (role + '.log')).open('w')
        logs.append(log)
        processes.append(subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT))
    if args.reconnect:
        deadline = time.monotonic() + 35
        while 'OPENRTS_NETWORK_BREAK:requested' not in (output / 'join.log').read_text():
            if time.monotonic() > deadline or any(p.poll() is not None for p in processes): raise RuntimeError('Guest did not reach the disconnect boundary')
            time.sleep(.05)
        with relay_lock:
            for connection in relay_connections:
                try: connection.shutdown(socket.SHUT_RDWR)
                except OSError: pass
    for process in processes:
        process.wait(timeout=55)
    hashes = []
    map_identities = []
    for role, process in zip(roles, processes):
        text = (output / (role + '.log')).read_text()
        if process.returncode or 'OPENRTS_NETWORK_INPUT:' not in text or 'OPENRTS_NETWORK_MENU:' not in text:
            raise RuntimeError(role + ' failed:\n' + text)
        results = [line.split(':', 1)[1] for line in text.splitlines() if line.startswith('OPENRTS_NETWORK_PASS:')]
        if len(results) != 1:
            raise RuntimeError(role + ' missing final result:\n' + text)
        if args.net3 and 'OPENRTS_NETWORK_PAUSE:' not in text:
            raise RuntimeError(role + ' did not observe agreed pause')
        hashes.extend(results)
        map_identities.append(next(line for line in text.splitlines() if line.startswith('OPENRTS_NETWORK_MAP:')))
    if args.lan_address and ('OPENRTS_LAN_JOIN:' + ('' if args.lan_address == 'auto' else args.lan_address)) not in (output / 'join.log').read_text():
        raise RuntimeError('Guest did not enter the locked room via the requested address')
    if len(set(hashes)) != 1:
        raise RuntimeError('Final hashes differ: ' + str(hashes))
    if len(set(map_identities)) != 1:
        raise RuntimeError('Map identities differ: ' + str(map_identities))
    if args.reconnect:
        text = (output / 'join.log').read_text()
        if 'OPENRTS_NETWORK_RECONNECT_BUTTON:pressed' not in text or 'OPENRTS_NETWORK_REJOIN:2' not in text:
            raise RuntimeError('Guest did not return through the native reconnect button:\n' + text)
        journals = []
        for role in roles:
            paths = [line.split(':', 1)[1] for line in (output / (role + '.log')).read_text().splitlines() if line.startswith('OPENRTS_NETWORK_JOURNAL:')]
            if len(paths) != 1: raise RuntimeError(role + ' did not report exactly one completed journal')
            journals.append(Path(paths[0]).read_bytes())
        if journals[0] != journals[1]: raise RuntimeError('Rejoined guest did not retain the common journal')
    print('PASS Godot network:', len(roles), 'TLS peers, lobby Ready/start, local ownership, production, local menu, agreed stop, offline replay' + (', fourth AI slot and agreed pause' if args.net3 else ''))
    print('State hash:', hashes[0])
    if args.reconnect: print('PASS Godot rejoin: transport break, failure screen, reconnect button, epoch rotation, continued ticks and replay')
    print('Artifacts:', output)
finally:
    if relay is not None: relay_stop.set(); relay.close()
    for process in processes:
        if process.poll() is None:
            process.kill()
            process.wait()
    for log in logs:
        log.close()

#!/usr/bin/env python3
"""Real kernel TCP loss/reordering in a disposable NET_ADMIN namespace, TLS end-to-end."""
import argparse
import json
from pathlib import Path
import socket
import sys
import subprocess
import tempfile
import time
import uuid

p = argparse.ArgumentParser()
p.add_argument('--output', type=Path)
p.add_argument('--image', default='openrts-netem:net2')
p.add_argument('--quick', action='store_true')
a = p.parse_args()
root = Path(__file__).resolve().parent.parent
out = (a.output or Path(tempfile.mkdtemp(prefix='openrts-netem-'))).resolve()
out.mkdir(parents=True, exist_ok=True)
dll = root / 'tests/OpenRTS.Network.Tests/bin/Debug/net10.0/OpenRTS.Network.Tests.dll'
if not dll.exists():
    raise RuntimeError('Build the solution first')
subprocess.run(['docker', 'info'], stdout=subprocess.DEVNULL, check=True)
subprocess.run(['docker', 'image', 'inspect', a.image], stdout=subprocess.DEVNULL, check=True)
image_id = subprocess.check_output(['docker', 'image', 'inspect', '--format', '{{.Id}}', a.image], text=True).strip()

def free_port():
    with socket.socket() as probe:
        probe.bind(('0.0.0.0', 0))
        return probe.getsockname()[1]

def percentile(values, q):
    values = sorted(v for v in values if v is not None)
    return round(values[min(len(values)-1, int((len(values)-1)*q+0.99999))], 3) if values else None


def wait_for_log(process, log, marker, timeout):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if marker in log.read_text(encoding='utf-8').splitlines():
            return
        if process.poll() is not None:
            break
        time.sleep(0.05)
    raise RuntimeError(f'{marker} was not reported. See {log}')


results = []
profiles = [(150, 20, 1, 8)] if a.quick else [(0, 0, 0, 2), (50, 20, 0, 4), (150, 20, 1, 8), (300, 0, 0, 12), (300, 80, 5, 12)]
for rtt, jitter, loss, delay in profiles:
    folder = out / f'rtt{rtt}-j{jitter}-loss{loss}'
    folder.mkdir(exist_ok=True)
    pin = folder / 'host.pin'
    pin.unlink(missing_ok=True)
    name = 'openrts-netem-' + uuid.uuid4().hex[:12]
    procs, logs = [], []
    host_port, relay_port = free_port(), free_port()
    try:
        def start(command, logname):
            log = (folder / logname).open('w')
            logs.append(log)
            process = subprocess.Popen(command, cwd=root, stdout=log, stderr=log)
            procs.append(process)
            return process
        def peer(role, address, port):
            return ['dotnet', str(dll), '--tls-peer', '--' + role + '=' + address, '--port=' + str(port),
                    '--delay=' + str(delay), '--ticks=' + str(200 if loss == 1 else 100), '--pin-file=' + str(pin),
                    '--journal=' + str(folder / (role + '.journal')), '--metrics=' + str(folder / (role + '.json'))]
        host = start(peer('host', '0.0.0.0', host_port), 'host.log')
        wait_for_log(host, folder / 'host.log', f'LISTEN {host_port}', 10)
        host_gateway = ['--add-host=host.docker.internal:host-gateway'] if sys.platform.startswith('linux') else []
        relay = start(['docker', 'run', '--rm', '--name', name, '--cap-add', 'NET_ADMIN', *host_gateway,
                       '-p', f'127.0.0.1:{relay_port}:7000', a.image, '--upstream-port', str(host_port),
                       '--rtt', str(rtt), '--jitter', str(jitter), '--loss', str(loss)], 'relay.log')
        wait_for_log(relay, folder / 'relay.log', 'READY', 15)
        kernel = subprocess.check_output(['docker', 'exec', name, 'uname', '-r'], text=True).strip()
        join = start(peer('join', '127.0.0.1', relay_port), 'join.log')
        host.wait(timeout=55)
        join.wait(timeout=55)
        relay.wait(timeout=10)
        if any(process.returncode != 0 for process in (host, join, relay)):
            raise RuntimeError('Peer/relay failed; see ' + str(folder))
        host_data = json.loads((folder / 'host.json').read_text())
        join_data = json.loads((folder / 'join.json').read_text())
        if host_data['result'] != join_data['result']:
            raise AssertionError('TLS peers differ')
        text = (folder / 'relay.log').read_text()
        stats = json.loads(text.split('NETEM_STATS:', 1)[1].strip())
        netem = next(row for row in stats if row['kind'] == 'netem')
        if loss and netem['drops'] < 1:
            raise AssertionError('No kernel packet drops were exercised')
        samples = host_data['samples']
        steady = [sample for sample in samples if 30 <= sample['Tick'] <= 90]
        hz = 1000 * (steady[-1]['Tick']-steady[0]['Tick']) / (steady[-1]['ElapsedMilliseconds']-steady[0]['ElapsedMilliseconds'])
        if rtt <= 50 and loss == 0 and hz < 22:
            raise AssertionError('Pipeline missed the >=22Hz low-loss baseline')
        if max(sample['SendQueueHighWater'] for sample in samples) > 32 or max(sample['ReceiveQueueHighWater'] for sample in samples) > 32:
            raise AssertionError('Pipeline queue bounds exceeded')
        result = dict(rtt=rtt, jitter=jitter, loss=loss, inputDelay=delay, steadyHz=round(hz, 2),
                      meanSimulationHz=round(samples[-1]['Tick']*1000/samples[-1]['ElapsedMilliseconds'],2),
                      simulationElapsedMs=round(samples[-1]['ElapsedMilliseconds'],1),
                      maxWaitMs=round(max(s['WaitMilliseconds'] for s in samples),3),
                      maxUnverifiedTicks=max(s['Tick']-s['VerifiedTick'] for s in samples),
                      waitP95=percentile([s['WaitMilliseconds'] for s in samples], .95),
                      sealedToAppliedP95=percentile([s['SealedToAppliedMilliseconds'] for s in samples], .95),
                      submitToObservedFrameMs=host_data['submitToObservedFrameMs'],
                      sendHighWater=max(s['SendQueueHighWater'] for s in samples),
                      receiveHighWater=max(s['ReceiveQueueHighWater'] for s in samples),
                      final=host_data['result'], netem=netem, kernel=kernel, environment=host_data['environment'])
        results.append(result)
        print(json.dumps(result), flush=True)
    finally:
        subprocess.run(['docker', 'rm', '-f', name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        for process in procs:
            if process.poll() is None:
                process.kill()
            process.wait()
        for log in logs:
            log.close()
(out / 'results.json').write_text(json.dumps(dict(image=image_id, profiles=results), indent=2))
print('PASS real TCP/TLS netem profiles; both replay files verified. Artifacts:', out)

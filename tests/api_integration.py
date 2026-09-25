"""Hardware-free HTTP integration checks using isolated temporary settings."""
import json
from pathlib import Path
import secrets
import socket
import subprocess
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


def main():
    root = Path(__file__).resolve().parents[1]
    exe = root / 'build' / 'Godox.Desktop.exe'
    if not exe.exists():
        raise RuntimeError('Run setup.cmd first.')
    with tempfile.TemporaryDirectory(prefix='godox-api-test-') as temporary:
        folder = Path(temporary)
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0))
            port = listener.getsockname()[1]
        token = secrets.token_hex(24)
        profiles = [dict(id=f'demo-{i}', name=f'Lamp {i}', model='SL60IIBi',
                         address=f'02:00:00:00:00:{i:02X}', statePath=str(folder / f'{i}_mesh_state.json'),
                         mixerLevel=100 // i, preferredCct=4000, autoConnect=False) for i in (1, 2)]
        settings = dict(devices=profiles, apiPort=port, apiToken=token, apiEnabled=True,
                        hotkeysEnabled=False, autoConnectEnabled=False,
                        pythonPath=str(root / 'test' / '.venv' / 'Scripts' / 'python.exe'))
        (folder / 'settings.json').write_text(json.dumps(settings), encoding='utf-8')

        def call(path, body=None, method='POST', authenticated=True):
            headers = {'Content-Type': 'application/json'}
            if authenticated:
                headers['X-Godox-Token'] = token
            data = None if body is None else json.dumps(body).encode()
            request = Request(f'http://127.0.0.1:{port}' + path, data=data, headers=headers, method=method)
            try:
                with urlopen(request, timeout=10) as response:
                    return response.status, json.load(response)
            except HTTPError as exc:
                content = exc.read()
                return exc.code, json.loads(content) if content else None

        process = subprocess.Popen([str(exe), '--headless', '--root', str(root), '--data-dir', str(folder)],
                                   creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            for _ in range(100):
                if process.poll() is not None:
                    raise RuntimeError('Test HTTP host exited during startup.')
                try:
                    if call('/health', method='GET', authenticated=False)[0] == 200:
                        break
                except URLError:
                    time.sleep(.1)
            else:
                raise TimeoutError('HTTP host did not start.')
            assert call('/api/devices', method='GET', authenticated=False)[0] == 401
            code, devices = call('/api/devices', method='GET')
            assert code == 200 and len(devices) == 2 and not any(d['connected'] for d in devices)
            assert all('statePath' not in d and 'networkKey' not in d for d in devices)
            assert call('/api/devices/missing/connect')[0] == 404
            assert call('/api/devices/demo-1/set', {'brightness': 10, 'cct': 4000})[0] == 409
            assert call('/api/mixer', {'level': 101})[0] == 400
            assert call('/api/mixer', {'level': 50})[0] == 200
            _, devices = call('/api/devices', method='GET')
            assert [d['level'] for d in devices] == [100, 50]
            assert [d['effectiveBrightness'] for d in devices] == [50, 25]
            assert call('/api/devices/demo-2/mixer', {'muted': True})[0] == 200
            assert call('/api/mixer', {'muted': True})[0] == 200
            _, devices = call('/api/devices', method='GET')
            assert all(d['effectiveBrightness'] == 0 for d in devices)
            assert call('/api/mixer', {'muted': False})[0] == 200
            _, devices = call('/api/devices', method='GET')
            assert [d['effectiveBrightness'] for d in devices] == [50, 0]
            assert not list(folder.rglob('*_mesh_state.json')), 'Offline checks must not create Mesh keys'
            print('PASS: HTTP auth, isolated profiles, validation, offline rejection, mixer proportions and independent mute.')
        finally:
            # Own headless test process only; no BLE connections have been opened.
            process.terminate()
            process.wait(timeout=10)


if __name__ == '__main__':
    main()

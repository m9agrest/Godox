"""Explicit hardware check: low-brightness mixer exercise, restore values, close UI.
Uses isolated app settings but the ORIGINAL shared Mesh state files/counters.
Run with --hardware after closing Godox Desktop and the phone app.
"""
import argparse
import ctypes
import json
import os
from pathlib import Path
import statistics
import subprocess
import time
import urllib.request
import uuid

parser = argparse.ArgumentParser()
parser.add_argument('--hardware', action='store_true', required=True)
parser.parse_args()
root = Path(__file__).resolve().parents[1]
fixture = root / 'artifacts' / 'mixer-hardware-test'
(fixture / 'data').mkdir(parents=True, exist_ok=True)
(fixture / 'backend').mkdir(exist_ok=True)
(fixture / 'backend' / 'worker.py').write_bytes((root / 'backend' / 'worker.py').read_bytes())
original = json.loads((Path(os.environ['LOCALAPPDATA']) / 'Godox' / 'settings.json').read_text(encoding='utf-8-sig'))
profiles = [dict(p, mixerLevel=None, muted=False, autoConnect=False) for p in original['devices']]
token = uuid.uuid4().hex
config = dict(devices=profiles, apiEnabled=True, apiPort=18766, apiToken=token,
              hotkeysEnabled=False, autoConnectEnabled=False, masterLevel=100,
              pythonPath=str(root / 'test' / '.venv' / 'Scripts' / 'python.exe'))
(fixture / 'data' / 'settings.json').write_text(json.dumps(config), encoding='utf-8')
startup = subprocess.STARTUPINFO()
startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
startup.wShowWindow = 0
process = subprocess.Popen([str(root / 'build' / 'Godox.Desktop.exe'), '--root', str(fixture),
                            '--data-dir', str(fixture / 'data')], startupinfo=startup)

def request(path, body=None, method='POST'):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request('http://127.0.0.1:18766' + path, data=data, method=method,
                                 headers={'X-Godox-Token': token, 'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=110) as response:
        return json.load(response)

baseline = {}
report = []
try:
    for _ in range(100):
        try:
            request('/health', method='GET')
            break
        except OSError:
            time.sleep(.1)
    for profile in profiles:
        ident = profile['id']
        view = request(f'/api/devices/{ident}/connect')
        assert view['connected'] and view['warning'] is None, view.get('warning')
        baseline[ident] = (view['brightness'], view['cct'])
        print(profile['model'], 'baseline', baseline[ident], flush=True)
    for i, ident in enumerate(baseline):
        request(f'/api/devices/{ident}/mixer', {'level': 4 + i * 2})
    request('/api/mixer', {'level': 50})
    time.sleep(.25)
    for i, ident in enumerate(baseline):
        actual = request(f'/api/devices/{ident}/status')
        assert actual['brightness'] == 2 + i, actual
        assert actual['level'] == 4 + i * 2, actual
        durations = []
        for _ in range(12):
            started = time.perf_counter()
            request(f'/api/devices/{ident}/mixer', {'level': 4 + i * 2})
            durations.append((time.perf_counter() - started) * 1000)
        report.append(dict(model=next(p['model'] for p in profiles if p['id'] == ident),
                           samples=len(durations), median_ms=round(statistics.median(durations), 1),
                           max_ms=round(max(durations), 1), verified_brightness=actual['brightness']))
    request('/api/mixer', {'muted': True})
    time.sleep(.5)
    for ident in baseline:
        actual = request(f'/api/devices/{ident}/status')
        assert actual['brightness'] == 0, actual
    request('/api/mixer', {'muted': False})
    print(json.dumps(report, ensure_ascii=True))
finally:
    try:
        request('/api/mixer', {'muted': True, 'level': 100})
        for ident, (brightness, cct) in baseline.items():
            request(f'/api/devices/{ident}/set', {'brightness': brightness, 'cct': cct})
        request('/api/mixer', {'muted': False})
        time.sleep(.5)
        for ident, (brightness, cct) in baseline.items():
            actual = request(f'/api/devices/{ident}/status')
            assert (actual['brightness'], actual['cct']) == (brightness, cct), f'Restore mismatch: expected {(brightness,cct)}, received {(actual["brightness"],actual["cct"])}'
        request('/api/disconnect-all')
    finally:
        # WM_CLOSE only to windows belonging to this test process: exercise normal cleanup.
        callback_type = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
        def close_window(hwnd, _):
            pid = ctypes.c_ulong()
            ctypes.windll.user32.GetWindowThreadProcessId(ctypes.c_void_p(hwnd), ctypes.byref(pid))
            if pid.value == process.pid:
                ctypes.windll.user32.PostMessageW(ctypes.c_void_p(hwnd), 0x10, 0, 0)
            return True
        ctypes.windll.user32.EnumWindows(callback_type(close_window), 0)
        process.wait(timeout=30)
print('PASS: physical readback, master mute, restored original values, normal shutdown')

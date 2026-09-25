"""Test actual app launch modes and HTTP startup, without changing Windows startup."""
import argparse
import json
from pathlib import Path
import secrets
import socket
import subprocess
import uuid

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--exe', type=Path, default=root / 'build/Godox.Desktop.exe')
exe = parser.parse_args().exe.resolve()
program_root = exe.parent if (exe.parent / 'backend/worker.py').exists() else root
area = root / 'artifacts' / ('startup-smoke-' + uuid.uuid4().hex)
cases = [(True, True, False, True), (True, True, True, False),
         (True, False, True, True), (True, False, False, False),
         (False, True, True, True), (False, True, False, False)]
for index, (automatic, tray, close, api) in enumerate(cases):
    folder = area / str(index)
    folder.mkdir(parents=True)
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        port = listener.getsockname()[1]
    settings = dict(devices=[], apiPort=port, apiToken=secrets.token_hex(24), apiEnabled=api,
                    autoConnectEnabled=False, hotkeysEnabled=False, startInTray=tray, closeToTray=close)
    (folder / 'settings.json').write_text(json.dumps(settings), encoding='utf-8')
    command = [str(exe), '--root', str(program_root), '--data-dir', str(folder), '--startup-smoke']
    if automatic:
        command.append('--startup')
    process = subprocess.Popen(command, creationflags=subprocess.CREATE_NO_WINDOW)
    try:
        assert process.wait(timeout=30) == 0, 'Startup smoke exited with an error.'
    finally:
        if process.poll() is None:
            subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'], capture_output=True, check=False)
    report = json.loads((folder / 'artifacts/startup-smoke.json').read_text())
    visible = not (automatic and tray)
    assert report['visible'] == visible, (settings, report)
    assert report['visibleTransitions'] == int(visible), 'Hidden startup must never show the window, even briefly.'
    assert report['listening'] == api, 'HTTP server ignored its startup preference.'
    assert report['startInTray'] == tray and report['closeToTray'] == close
    assert not list(folder.glob('*_mesh_state.json'))
    print(f'PASS: automatic={automatic}, startInTray={tray}, closeToTray={close}, api={api}', flush=True)
print('Screenshots: ' + str(area))

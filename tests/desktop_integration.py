"""Exercise WPF tray/activation/button bindings with isolated, offline fixtures."""
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
args = parser.parse_args()
exe = args.exe.resolve()
program_root = exe.parent if (exe.parent / 'backend/worker.py').exists() else root
folder = root / 'artifacts' / ('desktop-smoke-' + uuid.uuid4().hex)
folder.mkdir(parents=True)
with socket.socket() as listener:
    listener.bind(('127.0.0.1', 0))
    port = listener.getsockname()[1]
settings = dict(apiPort=port, apiToken=secrets.token_hex(24), apiEnabled=True,
                autoConnectEnabled=False, hotkeysEnabled=True, closeToTray=True,
                devices=[dict(id=f'demo-{i}', name=f'Test lamp {i}', model='SL60IIBi',
                              address=f'02:00:00:00:00:{i:02X}', statePath=str(folder / f'{i}_mesh_state.json'),
                              mixerLevel=100 // i, preferredCct=4000, autoConnect=False,
                              hotkey='Ctrl+Alt+Shift+F11' if i == 1 else '') for i in (1, 2)])
(folder / 'settings.json').write_text(json.dumps(settings), encoding='utf-8')
process = subprocess.Popen([str(exe), '--root', str(program_root), '--data-dir', str(folder),
                            '--mixer-smoke', '--desktop-smoke'], creationflags=subprocess.CREATE_NO_WINDOW)
try:
    assert process.wait(timeout=45) == 0, 'Desktop UI smoke exited with an error.'
finally:
    if process.poll() is None:
        subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'], capture_output=True, check=False)
report = json.loads((folder / 'artifacts/ui-smoke.json').read_text())
assert report['connected'] == 0 and report['devices'] == 2 and report['hotkeyDelivered']
assert (folder / 'artifacts/ui-settings.png').is_file()
assert not list(folder.glob('*_mesh_state.json')), 'Offline test must not create real bindings.'
saved = json.loads((folder / 'settings.json').read_text(encoding='utf-8-sig'))
assert saved['closeToTray'] and not saved['autoConnectEnabled']
print('PASS: pending connection button, close-to-tray, background HTTP/hotkeys, second launch activation, startup hiding and explicit exit.')
print('Screenshots: ' + str(folder / 'artifacts'))

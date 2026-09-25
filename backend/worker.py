"""Persistent BLE worker. One JSON request/response per line; stdout is protocol only."""
import asyncio
import ctypes
from dataclasses import asdict, replace
import json
import logging
import os
from pathlib import Path
import re
import sys
import tempfile
import time

from bleak import BleakClient, BleakScanner
from godox_mesh_bt import GodoxController
from godox_mesh_bt.config_session import ConfigSession
from godox_mesh_bt.provisioning import ProvisioningSession
from godox_mesh_bt.state import MeshState

MODELS = {'SL60IIBi': 0x003A, 'P260C Pro': 0x0076}
PROVISION = '00001827-0000-1000-8000-00805f9b34fb'
PROXY = '00001828-0000-1000-8000-00805f9b34fb'


class NeedsProvisioning(ValueError):
    pass


def save_state(state, path):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode='w', encoding='utf-8', dir=path.parent, delete=False) as output:
            temporary = Path(output.name)
            json.dump(state.to_dict(), output)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


class SavedProvisioningSession(ProvisioningSession):
    def __init__(self, *args, state_path, **kwargs):
        super().__init__(*args, **kwargs)
        self.state_path = state_path
        self.app_key = os.urandom(16).hex()
        self.saved = False

    async def _exchange(self, client):
        state = await super()._exchange(client)
        state = replace(state, app_key=self.app_key, device_address=self.address)
        # Persist before the library's disconnect: that cleanup can fail after success.
        save_state(state, self.state_path)
        self.saved = True
        return state


def validate_device(device):
    if device.get('model') not in MODELS:
        raise ValueError('Поддерживаются SL60IIBi и P260C Pro.')
    address = device.get('address', '').upper()
    if not re.fullmatch(r'(?:[0-9A-F]{2}:){5}[0-9A-F]{2}', address):
        raise ValueError('MAC-адрес должен иметь вид 02:00:00:00:00:01.')
    if not device.get('statePath') or not Path(device['statePath']).is_absolute():
        raise ValueError('Нужен абсолютный путь к файлу привязки.')
    return address, Path(device['statePath']).resolve()


def validate_values(brightness, cct):
    if type(brightness) is not int or not 0 <= brightness <= 100:
        raise ValueError('Яркость: целое число от 0 до 100.')
    if type(cct) is not int or not 2800 <= cct <= 6500 or cct % 100:
        raise ValueError('Температура: 2800–6500 K с шагом 100 K.')


def process_alive(pid):
    if sys.platform != 'win32':
        return Path(f'/proc/{pid}').exists()
    kernel = ctypes.windll.kernel32
    kernel.OpenProcess.restype = ctypes.c_void_p
    kernel.GetExitCodeProcess.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong)]
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel.OpenProcess(0x1000, False, pid)
    if not handle:
        return kernel.GetLastError() == 5  # Access denied: conservatively assume alive.
    try:
        code = ctypes.c_ulong()
        return not kernel.GetExitCodeProcess(handle, ctypes.byref(code)) or code.value == 259
    finally:
        kernel.CloseHandle(handle)


class StateLock:
    """Compatible with the original test/godox_control.py lock names."""
    def __init__(self, state_path):
        self.path = state_path.with_name(state_path.stem.removesuffix('_mesh_state') + '.lock')
        self.file = None

    def acquire(self):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        if self.path.exists():
            try:
                pid = int(self.path.read_text().strip())
                if not process_alive(pid):
                    self.path.unlink()
            except (ValueError, OSError):
                pass
        try:
            self.file = self.path.open('x')
        except FileExistsError as exc:
            raise RuntimeError('Прибор уже используется другим процессом. Закройте тестовый контроллер.') from exc
        self.file.write(str(os.getpid()))
        self.file.flush()

    def release(self):
        if self.file is not None:
            self.file.close()
            self.file = None
            self.path.unlink(missing_ok=True)


class Session:
    def __init__(self, controller, lock, address):
        self.controller = controller
        self.lock = lock
        self.address = address
        self.brightness = None
        self.cct = None
        self.resume = 10
        self.warning = None

    def view(self):
        return dict(connected=self.controller.is_connected, brightness=self.brightness,
                    cct=self.cct, warning=self.warning)

    async def read(self):
        try:
            status = await self.controller.request_status(timeout=5)
            self.brightness = status.brightness
            self.cct = status.cct
            self.warning = None
            if status.cct is None or not 2800 <= status.cct <= 6500:
                self.warning = 'Прибор вернул недостоверную температуру. Сверьте экран и примените настройки.'
            if status.brightness > 0:
                self.resume = status.brightness
        except TimeoutError:
            self.warning = 'Соединение открыто, но прибор не ответил на запрос состояния.'
        return self.view()


class Worker:
    def __init__(self):
        self.sessions = {}

    async def _check_target(self, address, expected_service):
        device = await BleakScanner.find_device_by_address(address, timeout=15)
        if device is None:
            raise ValueError('Устройство не найдено. Проверьте питание и Bluetooth.')
        async with BleakClient(device, winrt={'use_cached_services': False}) as client:
            services = {service.uuid for service in client.services}
            if expected_service == PROXY and PROVISION in services:
                raise NeedsProvisioning('Bluetooth светильника сброшен. Нужно создать новую привязку.')
            if expected_service not in services:
                raise ValueError('Для новой привязки сначала сбросьте Bluetooth выбранного светильника.')
        return device

    async def scan(self):
        found = await BleakScanner.discover(timeout=8, return_adv=True)
        result = []
        for device, adv in found.values():
            name = adv.local_name or device.name or ''
            model = None
            for company, payload in adv.manufacturer_data.items():
                blob = int(company).to_bytes(2, 'little') + payload
                if len(blob) >= 10 and blob[:6][::-1].hex() == device.address.replace(':', '').lower():
                    model = next((key for key, code in MODELS.items() if code == int.from_bytes(blob[6:8], 'little')), None)
            if model or 'gd_led' in name.lower() or 'godox' in name.lower():
                result.append(dict(address=device.address, name=name, model=model, rssi=adv.rssi,
                                   needsProvisioning=PROVISION in adv.service_uuids))
        return sorted(result, key=lambda d: -d['rssi'])

    async def disconnect(self, ident):
        session = self.sessions.pop(ident, None)
        if session:
            try:
                await asyncio.wait_for(session.controller.disconnect(), timeout=8)
            finally:
                session.lock.release()
        return dict(connected=False, brightness=None, cct=None, warning=None)

    async def connect(self, device):
        ident = device['id']
        address, path = validate_device(device)
        old = self.sessions.get(ident)
        if old and old.controller.is_connected:
            return await old.read()
        await self.disconnect(ident)
        if not path.exists():
            raise ValueError('Нет файла привязки. Выберите существующий файл или создайте новую привязку.')
        lock = StateLock(path)
        lock.acquire()
        controller = None
        try:
            state = MeshState.load(path)
            if state.device_address.upper() != address:
                raise ValueError('MAC в файле привязки не совпадает с выбранным устройством.')
            target = await self._check_target(address, PROXY)
            controller = GodoxController(address, path,
                state_writer=lambda state: save_state(state, path),
                client_factory=lambda addr: BleakClient(target, winrt={'use_cached_services': False}))
            await controller.connect()
            session = Session(controller, lock, address)
            session.resume = device.get('resumeBrightness', 10)
            self.sessions[ident] = session
            return await session.read()
        except BaseException:
            self.sessions.pop(ident, None)
            if controller:
                try:
                    await asyncio.wait_for(controller.disconnect(), timeout=5)
                except Exception:
                    pass
            lock.release()
            raise

    async def provision(self, device, rebind_only=False):
        address, path = validate_device(device)
        if device['id'] in self.sessions:
            raise ValueError('Сначала отключите устройство.')
        lock = StateLock(path)
        lock.acquire()
        try:
            if not rebind_only:
                target = await self._check_target(address, PROVISION)
                if path.exists():
                    backup = path.parent / 'binding-backups'
                    backup.mkdir(exist_ok=True)
                    with (backup / f'{path.stem}.{time.time_ns()}.json').open('xb') as output:
                        output.write(path.read_bytes())
                session = SavedProvisioningSession(address, os.urandom(16), 0, 0, 2, state_path=path,
                    client_factory=lambda addr: BleakClient(target, winrt={'use_cached_services': False}))
                try:
                    await session.run()
                except Exception as exc:
                    if session.saved:
                        raise RuntimeError('Ключи уже сохранены. Нажмите «Завершить привязку»; повторный сброс не нужен.') from exc
                    raise
            state = MeshState.load(path)
            if state.device_address.upper() != address:
                raise ValueError('MAC в файле привязки не совпадает с выбранным устройством.')
            save_state(state.next_sequence(16), path)
            await ConfigSession(address, state,
                client_factory=lambda addr: BleakClient(addr, winrt={'use_cached_services': False})).run()
            return dict(connected=False, brightness=None, cct=None,
                        warning='Привязка сохранена. Нажмите «Подключить».')
        finally:
            lock.release()

    async def handle(self, request):
        command = request['command']
        if command == 'scan':
            return await self.scan()
        if command == 'sessions':
            return {ident: session.view() for ident, session in self.sessions.items()}
        if command == 'shutdown':
            for ident in list(self.sessions):
                await self.disconnect(ident)
            return {}
        device = request['device']
        validate_device(device)
        ident = device['id']
        if command == 'connect':
            return await self.connect(device)
        if command == 'disconnect':
            return await self.disconnect(ident)
        if command in ('provision', 'rebind'):
            return await self.provision(device, command == 'rebind')
        if command not in ('status', 'set', 'set_fast', 'toggle', 'off'):
            raise ValueError('Неизвестная команда.')
        # Validate before any write, including booleans/NaN in JSON.
        if command in ('set', 'set_fast'):
            validate_values(request.get('brightness'), request.get('cct'))
        session = self.sessions.get(ident)
        if session is None or not session.controller.is_connected:
            raise ValueError('Устройство отключено. Сначала нажмите «Подключить».')
        if command == 'status':
            return await session.read()
        if command in ('set', 'set_fast'):
            brightness, cct = request['brightness'], request['cct']
        else:
            await session.read()
            if command == 'toggle' and session.warning:
                raise ValueError('Состояние прибора не подтверждено. Примените яркость и температуру вручную.')
            if session.brightness is None:
                raise ValueError('Яркость неизвестна. Примените значения вручную.')
            brightness = 0 if command == 'off' or session.brightness > 0 else session.resume
            cct = session.cct if session.cct is not None and 2800 <= session.cct <= 6500 else device.get('preferredCct', 6500)
        validate_values(brightness, cct)
        await session.controller.set_params(brightness=brightness, cct=cct, min_kelvin=2800, max_kelvin=6500)
        if command == 'set_fast':
            # A successful BLE write is not a status acknowledgement. Explicit status
            # remains available; never block slider updates on a 5-second read timeout.
            session.brightness, session.cct = brightness, cct
            session.warning = None
            if brightness > 0:
                session.resume = brightness
            return session.view()
        await asyncio.sleep(0.2)
        result = await session.read()
        if result['brightness'] != brightness or result['cct'] != cct or result['warning']:
            result['warning'] = 'Команда отправлена, но ответ не подтверждает значения. Проверьте экран прибора.'
        return result


async def main():
    logging.basicConfig(stream=sys.stderr, level=logging.ERROR)
    worker = Worker()
    try:
        while line := await asyncio.to_thread(sys.stdin.readline):
            request = None
            try:
                request = json.loads(line)
                result = await asyncio.wait_for(worker.handle(request), timeout=90)
                response = dict(id=request.get('id'), ok=True, result=result)
            except Exception as exc:
                response = dict(id=request.get('id') if isinstance(request, dict) else None,
                                ok=False, error=f'{type(exc).__name__}: {exc}',
                                errorCode='needs_provisioning' if isinstance(exc, NeedsProvisioning) else None)
            print(json.dumps(response, ensure_ascii=False), flush=True)
            if isinstance(request, dict) and request.get('command') == 'shutdown':
                break
    finally:
        for ident in list(worker.sessions):
            try:
                await worker.disconnect(ident)
            except Exception:
                pass


if __name__ == '__main__':
    sys.stdin.reconfigure(encoding='utf-8')
    sys.stdout.reconfigure(encoding='utf-8')
    asyncio.run(main())

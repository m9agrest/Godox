"""Local two-light test controller; shares Godox Desktop's AppData bindings."""
import argparse
import asyncio
from dataclasses import asdict, replace
import json
import os
from pathlib import Path
import sys

from bleak import BleakClient, BleakScanner
from godox_mesh_bt import GodoxController
from godox_mesh_bt.provisioning import ProvisioningSession
from godox_mesh_bt.config_session import ConfigSession
from godox_mesh_bt.state import MeshState
from ble_probe import PROVISION, save

BASE = Path(__file__).resolve().parent
LIGHTS = {
    'sl60': 'SL60IIBi',
    'p260': 'P260C Pro',
}


def status_dict(status):
    result = asdict(status)
    result['raw'] = status.raw.hex()
    return result


async def run(args, path):
    model, address = LIGHTS[args.light], args.address
    print(f'{model}: {address}', flush=True)
    if args.command == 'provision':
        if path.exists():
            raise RuntimeError(f'State already exists: {path}. Use status or rebind; do not overwrite keys.')
        print('Waiting for Bluetooth-reset light (Mesh Provisioning service)...', flush=True)
        device = await BleakScanner.find_device_by_address(address, timeout=20)
        if device is None:
            raise RuntimeError('Target is not in pairing mode. Reset its Bluetooth, close Godox Light, retry.')
        async with BleakClient(device, winrt={'use_cached_services': False}) as check:
            if not any(service.uuid == PROVISION for service in check.services):
                raise RuntimeError('Light is already provisioned. Bluetooth reset is required.')
        session = ProvisioningSession(address=address, net_key=os.urandom(16),
            key_index=0, iv_index=0, unicast_address=2,
            client_factory=lambda addr: BleakClient(addr, winrt={'use_cached_services': False}))
        state = await session.run()
        state = replace(state, app_key=os.urandom(16).hex(), device_address=address)
        state.save(path)
        print(f'Provisioning complete; keys saved privately to {path.name}.', flush=True)
        print('Run rebind next. Do not reset the light again.', flush=True)
        return

    if not path.exists():
        raise RuntimeError(f'Missing {path.name}. Provision this light first.')
    state = MeshState.load(path)
    if state.device_address.upper() != address:
        raise RuntimeError('State address does not match the selected light.')
    if args.command == 'rebind':
        # Reserve config sequence numbers before transmission, including failure paths.
        state.next_sequence(16).save(path)
        session = ConfigSession(address, state, client_factory=lambda addr: BleakClient(addr, winrt={'use_cached_services': False}))
        await session.run()
        print('Application key binding sent; checking device status.', flush=True)
    async with GodoxController(address, path) as light:
        if args.command == 'verify':
            reported = status_dict(await light.request_status(timeout=6))
            original = dict(reported)
            if args.original_brightness is not None:
                original.update(brightness=args.original_brightness, cct=args.original_cct)
            if original['effect'] is not None or original['cct'] is None or not 2800 <= original['cct'] <= 6500:
                raise RuntimeError('Verification requires CCT mode with valid readback; nothing changed.')
            trial_brightness = original['brightness'] + (1 if original['brightness'] < 100 else -1)
            trial_cct = original['cct'] + (100 if original['cct'] < 6500 else -100)
            report = dict(model=model, address=address, original=original, original_reported=reported)
            try:
                print(f'Temporary test: {trial_brightness}% / {trial_cct} K', flush=True)
                await light.set_params(brightness=trial_brightness, cct=trial_cct, min_kelvin=2800, max_kelvin=6500)
                await asyncio.sleep(1)
                changed = status_dict(await light.request_status(timeout=6))
                report['changed'] = changed
                print('Test readback:', json.dumps(changed), flush=True)
                if changed['brightness'] != trial_brightness or changed['cct'] != trial_cct:
                    raise RuntimeError('Test readback does not match requested values.')
                report['change_verified'] = True
            finally:
                try:
                    await light.set_params(brightness=original['brightness'], cct=original['cct'], min_kelvin=2800, max_kelvin=6500)
                    await asyncio.sleep(0.5)
                    restored = status_dict(await light.request_status(timeout=6))
                    report['restored'] = restored
                    report['restore_verified'] = (restored['brightness'], restored['cct']) == (original['brightness'], original['cct'])
                    print('Restored readback:', json.dumps(restored), flush=True)
                    if not report['restore_verified']:
                        raise RuntimeError('RESTORE NOT CONFIRMED. Check light settings manually.')
                finally:
                    save('verification', report)
            print('Hardware change and restoration verified.', flush=True)
            return
        if args.command == 'set':
            await light.set_params(brightness=args.brightness, cct=args.cct,
                                   min_kelvin=2800, max_kelvin=6500)
            await asyncio.sleep(0.5)
        status = status_dict(await light.request_status(timeout=6))
        print(json.dumps(status), flush=True)
        save('status', dict(model=model, address=address, command=args.command, status=status))
        if args.command == 'set':
            if status['brightness'] != args.brightness or status['cct'] != args.cct:
                raise RuntimeError('Command sent, but readback differs. Check the light panel and saved status.')
            print('Requested brightness and CCT confirmed by device readback.', flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('light', choices=LIGHTS)
    parser.add_argument('--device-id', help='Profile ID from Godox Desktop; required when several lights share a model')
    sub = parser.add_subparsers(dest='command', required=True)
    for name in ('provision', 'rebind', 'status'):
        sub.add_parser(name)
    verifier = sub.add_parser('verify')
    verifier.add_argument('--original-brightness', type=int, choices=range(0, 101), metavar='0..100')
    verifier.add_argument('--original-cct', type=int, metavar='2800..6500')
    setter = sub.add_parser('set')
    setter.add_argument('--brightness', type=int, required=True, choices=range(0, 101), metavar='0..100')
    setter.add_argument('--cct', type=int, required=True, metavar='2800..6500')
    args = parser.parse_args()
    if args.command == 'set' and (not 2800 <= args.cct <= 6500 or args.cct % 100):
        parser.error('CCT must be 2800..6500 K in steps of 100 K.')
    if args.command == 'verify':
        if (args.original_brightness is None) != (args.original_cct is None):
            parser.error('Provide both original values from the light panel, or neither.')
        if args.original_cct is not None and (not 2800 <= args.original_cct <= 6500 or args.original_cct % 100):
            parser.error('Original CCT must be 2800..6500 K in steps of 100 K.')
    data_dir = Path(os.environ['LOCALAPPDATA']) / 'Godox'
    settings_path = data_dir / 'settings.json'
    if not settings_path.exists():
        parser.error('Add the light in Godox Desktop first.')
    settings = json.loads(settings_path.read_text(encoding='utf-8-sig'))
    matches = [p for p in settings['devices'] if p['model'] == LIGHTS[args.light]
               and (args.device_id is None or p['id'] == args.device_id)]
    if len(matches) != 1:
        parser.error('Select exactly one configured light with --device-id PROFILE_ID (before the command).')
    profile = matches[0]
    args.address = profile['address'].upper()
    path = Path(profile['statePath'])
    path.parent.mkdir(parents=True, exist_ok=True)
    lock_path = path.with_name(path.stem.removesuffix('_mesh_state') + '.lock')
    try:
        # Refuse concurrent local commands sharing a sequence counter.
        with lock_path.open('x') as lock:
            lock.write(str(os.getpid()))
            lock.flush()
            try:
                asyncio.run(asyncio.wait_for(run(args, path), timeout=90))
            finally:
                lock.close()
                lock_path.unlink()
    except FileExistsError:
        print(f'Another command is using {args.light}; lock: {lock_path}', file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        return 130
    except Exception as exc:
        print(f'ERROR: {type(exc).__name__}: {exc}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())

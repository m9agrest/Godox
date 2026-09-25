"""Godox BLE diagnostics. No pairing, reset, provisioning or control writes."""
import argparse
import asyncio
from datetime import datetime, timezone
import json
from pathlib import Path
import sys

from bleak import BleakClient, BleakScanner

BASE = Path(__file__).resolve().parent
PROVISION = '00001827-0000-1000-8000-00805f9b34fb'
PROXY = '00001828-0000-1000-8000-00805f9b34fb'


def save(kind, data):
    folder = BASE / 'logs'
    folder.mkdir(exist_ok=True)
    path = folder / (datetime.now().strftime('%Y%m%d-%H%M%S-%f') + '-' + kind + '.json')
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding='utf-8')
    print(f'Report: {path}')


def classify(name, uuids):
    tags = []
    if any(part in name.lower() for part in ('godox', 'gd_led', 'sl60', 'p260')):
        tags.append('Godox name')
    if PROVISION in uuids:
        tags.append('Mesh: unprovisioned')
    if PROXY in uuids:
        tags.append('Mesh: provisioned; keys required')
    return tags


def identify_model(address, manufacturer_data):
    models = {0x003A: 'SL60IIBi', 0x0076: 'P260C Pro'}
    for company, payload in manufacturer_data.items():
        blob = int(company).to_bytes(2, 'little') + bytes(payload)
        # Godox embeds its reversed MAC before a little-endian model ID.
        if len(blob) >= 10 and blob[:6][::-1].hex() == address.replace(':', '').lower():
            radio_id = int.from_bytes(blob[6:8], 'little')
            return dict(radio_id=f'{radio_id:04X}', model=models.get(radio_id, 'Unknown Godox model'))
    return None


async def scan(seconds):
    print(f'Scanning BLE for {seconds:g} seconds...', flush=True)
    found = await asyncio.wait_for(BleakScanner.discover(timeout=seconds, return_adv=True), timeout=seconds + 20)
    records = []
    for device, adv in found.values():
        name = adv.local_name or device.name or ''
        uuids = sorted(set(adv.service_uuids) | set(adv.service_data))
        records.append(dict(address=device.address, name=name, rssi=adv.rssi,
                            identity=identify_model(device.address, adv.manufacturer_data),
                            service_uuids=uuids,
                            manufacturer_data={str(k): v.hex() for k, v in adv.manufacturer_data.items()},
                            service_data={k: v.hex() for k, v in adv.service_data.items()},
                            hints=classify(name, uuids)))
    records.sort(key=lambda x: (not bool(x['hints']), -x['rssi']))
    for item in records:
        print(f"{item['address']}  {item['rssi']:4} dBm  {item['name'] or '(unnamed)'}  {item['identity'] or ''}  {'; '.join(item['hints'])}")
    save('scan', dict(time=datetime.now(timezone.utc).isoformat(), devices=records))
    print(f'{len(records)} devices. Mesh advertisements alone do not prove Godox identity.')


async def inspect(address, seconds):
    print(f'Finding {address}...', flush=True)
    device = await BleakScanner.find_device_by_address(address, timeout=seconds)
    if device is None:
        raise RuntimeError('Device not advertising. Power it on and close the phone app, then scan again.')
    report = dict(time=datetime.now(timezone.utc).isoformat(), address=device.address,
                  name=device.name, connected=False, services=[])
    try:
        async with asyncio.timeout(seconds + 20):
            async with BleakClient(device, timeout=seconds, winrt={'use_cached_services': False}) as client:
                report['connected'] = client.is_connected
                print(f'Connected: {client.is_connected}; MTU: {client.mtu_size}', flush=True)
                for service in client.services:
                    row = dict(uuid=service.uuid, description=service.description, characteristics=[])
                    report['services'].append(row)
                    print(f'Service {service.uuid} ({service.description})', flush=True)
                    for char in service.characteristics:
                        entry = dict(uuid=char.uuid, handle=char.handle, properties=char.properties)
                        row['characteristics'].append(entry)
                        print(f'  {char.uuid} {char.properties}', flush=True)
                        # Read only standard device name/information; no proprietary reads or writes.
                        if 'read' in char.properties and (service.uuid.startswith('0000180a-') or char.uuid.startswith('00002a00-')):
                            try:
                                value = await asyncio.wait_for(client.read_gatt_char(char), timeout=5)
                                entry['value_hex'] = value.hex()
                                entry['value_text'] = bytes(value).decode('utf-8', errors='replace')
                                print(f"    {entry['value_text']!r}", flush=True)
                            except Exception as exc:
                                entry['read_error'] = f'{type(exc).__name__}: {exc}'
    except Exception as exc:
        report['error'] = f'{type(exc).__name__}: {exc}'
        raise
    finally:
        save('inspect', report)


async def adapter():
    if sys.platform != 'win32':
        raise RuntimeError('Adapter diagnostics currently support Windows only.')
    from winrt.windows.devices.bluetooth import BluetoothAdapter
    radio_adapter = await BluetoothAdapter.get_default_async()
    if radio_adapter is None:
        raise RuntimeError('No Bluetooth adapter found.')
    radio = await radio_adapter.get_radio_async()
    print(f'Adapter: {radio.name}; state: {radio.state.name}; BLE: {radio_adapter.is_low_energy_supported}')


def positive(value):
    value = float(value)
    if not 0 < value <= 120:
        raise argparse.ArgumentTypeError('Use a timeout between 0 and 120 seconds.')
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    sub.add_parser('adapter')
    scan_parser = sub.add_parser('scan')
    scan_parser.add_argument('--seconds', type=positive, default=20)
    inspect_parser = sub.add_parser('inspect')
    inspect_parser.add_argument('address', help='Exact BLE address from scan')
    inspect_parser.add_argument('--seconds', type=positive, default=20)
    args = parser.parse_args()
    try:
        if args.command == 'adapter':
            asyncio.run(adapter())
        elif args.command == 'scan':
            asyncio.run(scan(args.seconds))
        else:
            asyncio.run(inspect(args.address, args.seconds))
    except KeyboardInterrupt:
        return 130
    except Exception as exc:
        print(f'ERROR: {type(exc).__name__}: {exc}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())

import asyncio
from pathlib import Path
import sys
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'backend'))
import worker


class FakeController:
    fail_connect = False
    timeout_read = False
    def __init__(self, *args, **kwargs):
        self.is_connected = False
        self.brightness, self.cct = 0, 6500
        self.writes = []
    async def connect(self):
        self.is_connected = True
        if self.fail_connect:
            raise RuntimeError('connection interrupted')
    async def disconnect(self):
        self.is_connected = False
    async def request_status(self, **kwargs):
        if self.timeout_read:
            raise TimeoutError()
        return SimpleNamespace(brightness=self.brightness, cct=self.cct)
    async def set_params(self, *, brightness, cct, **kwargs):
        self.writes.append((brightness, cct))
        self.brightness, self.cct = brightness, cct


class WorkerTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.path = Path(self.temp.name) / 'sl60_mesh_state.json'
        worker.MeshState('00' * 16, '11' * 16, 1, 2, 10, 0,
            device_key='22' * 16, device_address='02:00:00:00:00:01').save(self.path)
        self.device = dict(id='sl60', address='02:00:00:00:00:01', model='SL60IIBi', statePath=str(self.path))
        self.worker = worker.Worker()
        self.patch = patch.object(worker, 'GodoxController', FakeController)
        self.patch.start()
        self.target_patch = patch.object(worker.Worker, '_check_target', AsyncMock(return_value=None))
        self.target_patch.start()
        FakeController.fail_connect = False
        FakeController.timeout_read = False

    async def asyncTearDown(self):
        for ident in list(self.worker.sessions):
            await self.worker.disconnect(ident)
        self.patch.stop()
        self.target_patch.stop()
        self.temp.cleanup()

    async def send(self, command, **values):
        return await self.worker.handle(dict(command=command, device=self.device, **values))

    async def test_connect_holds_lock_disconnect_releases_without_changing_light(self):
        self.assertTrue((await self.send('connect'))['connected'])
        controller = self.worker.sessions['sl60'].controller
        lock = worker.StateLock(self.path)
        with self.assertRaises(RuntimeError):
            lock.acquire()
        await self.send('disconnect')
        self.assertEqual(controller.writes, [])
        self.assertFalse(controller.is_connected)
        lock.acquire()
        lock.release()

    async def test_repeated_connect_reuses_session(self):
        await self.send('connect')
        first = self.worker.sessions['sl60']
        await self.send('connect')
        self.assertIs(first, self.worker.sessions['sl60'])

    async def test_set_requires_both_values_and_validates_before_writing(self):
        await self.send('connect')
        for brightness, cct in [(None, 4000), (True, 4000), (-1, 4000), (101, 4000), (5, 2700), (5, 4050)]:
            with self.assertRaises(ValueError):
                await self.send('set', brightness=brightness, cct=cct)
        self.assertEqual(self.worker.sessions['sl60'].controller.writes, [])

    async def test_toggle_restores_previous_nonzero_brightness(self):
        await self.send('connect')
        await self.send('set', brightness=37, cct=4300)
        self.assertEqual((await self.send('toggle'))['brightness'], 0)
        self.assertEqual((await self.send('toggle'))['brightness'], 37)

    async def test_disconnected_rejects_control(self):
        with self.assertRaises(ValueError):
            await self.send('set', brightness=10, cct=4000)

    async def test_fast_set_does_not_request_status_or_sleep(self):
        await self.send('connect')
        controller = self.worker.sessions['sl60'].controller
        controller.request_status = AsyncMock(side_effect=AssertionError('slow read in slider path'))
        with patch.object(worker.asyncio, 'sleep', AsyncMock(side_effect=AssertionError('slider sleep'))):
            result = await self.send('set_fast', brightness=25, cct=4000)
        self.assertEqual(controller.writes, [(25, 4000)])
        self.assertEqual(result['brightness'], 25)
        controller.request_status.assert_not_called()

    async def test_fast_set_still_validates_before_write(self):
        await self.send('connect')
        for brightness, cct in [(True, 4000), (101, 4000), (10, 4050)]:
            with self.assertRaises(ValueError):
                await self.send('set_fast', brightness=brightness, cct=cct)
        self.assertEqual(self.worker.sessions['sl60'].controller.writes, [])

    async def test_failed_connect_cleans_up_lock_and_session(self):
        FakeController.fail_connect = True
        with self.assertRaises(RuntimeError):
            await self.send('connect')
        self.assertEqual(self.worker.sessions, {})
        self.assertFalse(self.path.with_name('sl60.lock').exists())

    async def test_cancellation_cleans_up_lock(self):
        async def slow_connect(controller):
            controller.is_connected = True
            await asyncio.sleep(10)
        with patch.object(FakeController, 'connect', slow_connect):
            with self.assertRaises(TimeoutError):
                await asyncio.wait_for(self.send('connect'), 0.05)
        self.assertEqual(self.worker.sessions, {})
        self.assertFalse(self.path.with_name('sl60.lock').exists())

    async def test_address_mismatch_never_connects(self):
        self.device['address'] = '02:00:00:00:00:02'
        with self.assertRaises(ValueError):
            await self.send('connect')
        self.assertEqual(self.worker.sessions, {})

    async def test_read_timeout_reports_warning_without_faking_disconnect(self):
        FakeController.timeout_read = True
        status = await self.send('connect')
        self.assertTrue(status['connected'])
        self.assertIsNone(status['brightness'])
        self.assertTrue(status['warning'])

    async def test_provision_refuses_existing_keys(self):
        before = self.path.read_bytes()
        with patch.object(self.worker, '_check_target', AsyncMock(side_effect=ValueError('not reset'))):
            with self.assertRaises(ValueError):
                await self.send('provision')
        self.assertEqual(before, self.path.read_bytes())

    async def test_unknown_model_rejected(self):
        self.device['model'] = 'unverified model'
        with self.assertRaises(ValueError):
            await self.send('connect')

    async def test_new_keys_survive_disconnect_error_after_provisioning(self):
        self.path.unlink()
        state = worker.MeshState('00' * 16, '00' * 16, 1, 2, 1, 0, device_key='22' * 16)
        session = worker.SavedProvisioningSession(self.device['address'], bytes(16), 0, 0, 2, state_path=self.path)
        client = SimpleNamespace(connect=AsyncMock(), disconnect=AsyncMock(side_effect=RuntimeError('disconnect failed')))
        with patch.object(worker.ProvisioningSession, '_exchange', AsyncMock(return_value=state)), \
             patch('godox_mesh_bt.provisioning.ProvisioningClient', return_value=client):
            with self.assertRaises(RuntimeError):
                await session.run()
        restored = worker.MeshState.load(self.path)
        self.assertEqual(restored.device_address, self.device['address'])
        self.assertEqual(restored.app_key, session.app_key)
        self.assertEqual(restored.device_key, state.device_key)
        self.assertTrue((await self.send('connect'))['connected'])

    async def test_atomic_write_failure_preserves_previous_state(self):
        before = self.path.read_bytes()
        state = worker.MeshState.load(self.path).next_sequence()
        with patch.object(worker.os, 'replace', side_effect=OSError('disk error')):
            with self.assertRaises(OSError):
                worker.save_state(state, self.path)
        self.assertEqual(self.path.read_bytes(), before)

    async def test_reset_light_reports_need_for_binding_without_altering_old_keys(self):
        before = self.path.read_bytes()
        with patch.object(self.worker, '_check_target', AsyncMock(side_effect=worker.NeedsProvisioning('reset'))):
            with self.assertRaises(worker.NeedsProvisioning):
                await self.send('connect')
        self.assertEqual(self.path.read_bytes(), before)
        self.assertFalse(self.path.with_name('sl60.lock').exists())


if __name__ == '__main__':
    unittest.main()

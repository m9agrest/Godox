# Диагностика Bluetooth

Экспериментальные скрипты для Windows. Основное приложение описано в [README](../README.md).
Используется окружение `test/.venv`, создаваемое `setup.cmd`.

Из корня проекта в CMD:

```cmd
test\.venv\Scripts\python.exe test\ble_probe.py adapter
test\.venv\Scripts\python.exe test\ble_probe.py scan --seconds 30
test\.venv\Scripts\python.exe test\ble_probe.py inspect YOUR_MAC_ADDRESS
```

Эти команды читают информацию, не сбрасывают привязку и не меняют свет.
Результаты сохраняются в `test/logs`; они могут содержать адреса находящихся рядом
Bluetooth-устройств, поэтому исключены из Git.

## Тестовый контроллер

Сначала добавьте и привяжите прибор в Godox Desktop, затем отключите его в приложении.
Скрипт выбирает сохранённый профиль из `%LOCALAPPDATA%\Godox\settings.json` по модели
и использует тот же файл ключей и блокировку, что и приложение.

```cmd
test\.venv\Scripts\python.exe test\godox_control.py sl60 status
test\.venv\Scripts\python.exe test\godox_control.py p260 set --brightness 10 --cct 4000
```

Если приборов одной модели несколько, укажите ID из `GET /api/devices`:

```cmd
test\.venv\Scripts\python.exe test\godox_control.py sl60 --device-id PROFILE_ID status
```

`verify` временно меняет яркость и температуру, затем пытается вернуть исходные
значения. Для обычного использования предпочтительно основное приложение.
Команды `provision` и `rebind` оставлены для диагностики; новая привязка требует
сброса Bluetooth на светильнике. Не запускайте несколько контроллеров с копиями
одних ключей: счётчик Mesh-сообщений должен оставаться единым.

Библиотека: [ha-godox-mesh](https://github.com/binary-person/ha-godox-mesh),
MIT; ревизия закреплена в `requirements.txt`. Локальная исследовательская копия
в `test/reference` не входит в репозиторий.

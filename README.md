# ZPL Print Bridge

[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.8-blueviolet)](https://dotnet.microsoft.com/download/dotnet-framework/net48)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D4)](https://www.microsoft.com/windows)

**Мост между raw TCP-портом 9100 и локальным принтером.** Служба Windows принимает ZPL / ESC-POS / любые сырые данные по TCP и отправляет их напрямую в принтер, выбранный по маске имени. Позволяет печатать на USB-принтере так же, как на сетевом, — через `IP:9100`.

**Bridge between a raw TCP port (9100) and a local printer.** A Windows service receives ZPL / ESC-POS / any raw payload over TCP and sends it directly to a printer picked by name pattern. Lets you print to a USB printer as if it were a network one — via `IP:9100`.

---

## Содержание

- [Зачем это нужно](#зачем-это-нужно)
- [Как это работает](#как-это-работает)
- [Возможности](#возможности)
- [Требования](#требования)
- [Структура проекта](#структура-проекта)
- [Быстрый старт](#быстрый-старт)
- [Конфигурация](#конфигурация)
- [Установка как службы Windows](#установка-как-службы-windows)
- [Использование](#использование)
- [Логирование](#логирование)
- [Устранение неполадок](#устранение-неполадок)
- [Почему .NET Framework 4.8](#почему-net-framework-48)

---

## Зачем это нужно

Многие принтеры этикеток (Zebra, BSmart, Godex, TSC и др.) умеют принимать задания **напрямую на TCP-порт 9100** — это стандарт «raw printing». Программы вроде WMS, 1С, SAP или самописных ERP просто открывают `IP:9100` и отправляют туда ZPL — и принтер печатает. Это удобно:

- не нужны драйверы на клиенте;
- не нужна расшаренная печать через SMB;
- задание уходит мгновенно, минуя спулер.

Но если принтер подключён **по USB** к обычному ПК (например, к рабочей станции оператора), такой номер не пройдёт: ПК не слушает 9100 и не умеет «перекидывать» байты в USB. Именно эту проблему решает **ZPL Print Bridge**: он поднимает TCP-сервер на порту 9100 и передаёт всё принятое в USB-принтер через спулер Windows.

Схема взаимодействия:

```
 ┌───────────────┐      TCP :9100      ┌────────────────────┐      USB       ┌──────────────┐
 │  WMS / 1С /   │ ──────────────────► │  ZplPrintBridge    │ ─────────────► │  Zebra GK420 │
 │  любая ERP    │      raw ZPL        │  (этот сервис)     │  через спулер  │  BSmart и т.д│
 └───────────────┘                     └────────────────────┘                └──────────────┘
```

---

## Как это работает

1. Служба при старте читает `App.config`, находит принтер по маске имени (`*Zebra*`, `*BSmart*` и т.п.) через `EnumPrinters`.
2. Поднимает `TcpListener` на `0.0.0.0:9100`.
3. Клиент подключается и отправляет поток байт (обычно — ZPL-метка, заканчивается на `^XZ`), после чего закрывает соединение.
4. Служба читает весь поток до EOF и передаёт буфер в принтер через WinAPI `OpenPrinter` / `WritePrinter` с типом документа `RAW`.
5. По желанию клиенту отправляется `OK\n`, чтобы он знал, что задание принято.

Если принтер переименовали или переустановили — служба автоматически переискивает его по маске при первой ошибке `Win32 1801 (ERROR_INVALID_PRINTER_NAME)` и повторяет печать того же задания.

---

## Возможности

- **Zero NuGet.** Только `System.ServiceProcess` и `System.Configuration` из GAC — никаких конфликтов версий и `FileLoadException`.
- **Поиск принтера по маске.** `*Zebra*`, `*BSmart*`, `ZDesigner*` — с wildcards `*` и `?`, регистронезависимо.
- **Автовосстановление.** Если принтер пропал / переименован — переискивание и повтор печати в том же запросе.
- **Консольный режим.** Тот же `.exe` запускается и как служба, и как обычная консольная программа — удобно для отладки.
- **Простой лог.** Пишется в файл рядом с exe; в консольном режиме дублируется в терминал.
- **Без драйвера на клиенте.** Клиенту достаточно открыть `TCP IP:9100`.
- **Настраивается без пересборки.** Все параметры — в `App.config`.

---

## Требования

| Что | Минимум |
|---|---|
| ОС | Windows 7 SP1 / Server 2008 R2 или новее |
| Платформа | .NET Framework 4.8 ([скачать](https://dotnet.microsoft.com/download/dotnet-framework/net48)) |
| Сборка | .NET SDK 6.0+ **или** Visual Studio 2022 |
| Права | Локальный администратор (для установки службы) |

> На Windows 10 21H2+ и Windows Server 2022 .NET Framework 4.8 уже установлен — доустанавливать ничего не нужно.

---

## Структура проекта

```
ZplPrintBridge/
├── App.config                     # Конфигурация (порт, принтер, лог)
├── Program.cs                     # Точка входа, выбор режима (служба / консоль)
├── ZplPrintBridge.csproj          # Файл проекта
├── ZplPrintBridgeService.cs       # Логика службы: TcpListener + печать
├── RawPrinterHelper.cs            # WinAPI для raw-печати через спулер
├── PrinterResolver.cs             # EnumPrinters + поиск по маске
├── install-service.bat            # Установка службы (копирование с шары + sc create)
├── uninstall-service.bat          # Удаление службы
├── README.md
└── LICENSE
```

---

## Быстрый старт

### Сборка

```powershell
git clone https://github.com/<your-user>/ZplPrintBridge.git
cd ZplPrintBridge
dotnet publish -c Release
```

Результат: `bin\Release\net48\publish\`. Внутри — только `ZplPrintBridge.exe` и `ZplPrintBridge.exe.config`. Всё остальное (System.*, WinAPI-обёртки) берётся из GAC и системы.

### Проверка в консольном режиме (без установки)

Запустите `ZplPrintBridge.exe` двойным кликом или из терминала. В окне появится примерно такой вывод:

```
[2026-09-29 12:00:00] === ZPL Print Bridge starting ===
[2026-09-29 12:00:00] Available printers (3):
[2026-09-29 12:00:00]   - ZDesigner GK420t
[2026-09-29 12:00:00]   - HP Universal Printing PCL 5
[2026-09-29 12:00:00]   - Microsoft Print to PDF
[2026-09-29 12:00:00] Resolved printer: ZDesigner GK420t
[2026-09-29 12:00:00] Listening on 0.0.0.0:9100
Running in console mode. Press Enter to stop.
```

Теперь из другого окна PowerShell отправьте тестовую метку:

```powershell
$zpl = "^XA^FO50,50^A0N,50,50^FDHello, ZPL!^FS^XZ"
$client = [System.Net.Sockets.TcpClient]::new("127.0.0.1", 9100)
$bytes = [Text.Encoding]::ASCII.GetBytes($zpl)
$client.GetStream().Write($bytes, 0, $bytes.Length)
$client.Close()
```

Если принтер подключён и настроен — метка напечатается. В консоли появится строка `Sent 43 bytes to 'ZDesigner GK420t' (client 127.0.0.1:xxxxx)`.

---

## Конфигурация

Все параметры — в `App.config` (при сборке превращается в `ZplPrintBridge.exe.config`).

| Ключ | Тип | По умолчанию | Описание |
|---|---|---|---|
| `PrinterNamePatterns` | строка | `*Zebra*,*BSmart*` | Маски имён через запятую. Побеждает первое совпадение. Поддерживает `*` и `?`. |
| `PrinterName` | строка | (пусто) | Точное имя принтера. Если задано — маски игнорируются. |
| `Port` | int | `9100` | TCP-порт прослушивания. |
| `ListenAddress` | строка | `0.0.0.0` | Адрес привязки. `0.0.0.0` — все интерфейсы, `127.0.0.1` — только локально. |
| `SendAck` | bool | `true` | Отправлять ли `OK\n` после успешной печати. |
| `ResolveOnEveryPrint` | bool | `false` | Переискивать принтер при каждой печати. Медленнее, но всегда актуально. |
| `LogFile` | строка | `ZplPrintBridge.log` | Путь к логу. Относительные пути — относительно папки с exe. |

### Примеры масок

| Маска | Что найдёт |
|---|---|
| `*Zebra*` | Любой принтер, содержащий «Zebra» в имени (ZDesigner GK420t, Zebra ZT230, …) |
| `ZDesigner*` | Все принтеры, начинающиеся на «ZDesigner» |
| `*GK420*` | Все принтеры модели GK420 |
| `*BSmart*` | Принтеры BSmart |

### Изменение конфига без пересборки

1. Откройте `C:\Services\ZplPrintBridge\ZplPrintBridge.exe.config` в блокноте (или `ZplPrintBridge.exe.config` рядом с exe в publish).
2. Поправьте нужный `<add key="..." value="..." />`.
3. Перезапустите службу:

```powershell
Restart-Service ZplPrintBridge
```

---

## Установка как службы Windows

Готовый `install-service.bat` копирует файлы с сетевой шары в `C:\Services\ZplPrintBridge` и регистрирует службу. Перед первым запуском откройте `.bat` и настройте:

```bat
set "SOURCE=\\fileserver\share\ZplPrintBridge\publish"     REM откуда копировать
set "TARGET=C:\Services\ZplPrintBridge"                    REM куда положить
set "SHARE_USER="                                          REM креды для шары (если нужны)
set "SHARE_PASS="
set "SVC_USER=LocalSystem"                                 REM от кого работать
set "SVC_PASS="
```

Затем **запустите `.bat` от имени администратора**. Скрипт:

1. Подключится к шаре (если указаны креды).
2. Остановит и удалит старую версию службы, если она была.
3. Скопирует файлы в `TARGET`.
4. Зарегистрирует службу с автозапуском.
5. Настроит автоматический перезапуск при сбое (5 с / 10 с / 30 с).
6. Запустит службу и покажет её состояние.

### Ручная установка (без скрипта)

```powershell
mkdir C:\Services\ZplPrintBridge
Copy-Item .\bin\Release\net48\publish\* C:\Services\ZplPrintBridge\ -Recurse

sc.exe create ZplPrintBridge binPath= "C:\Services\ZplPrintBridge\ZplPrintBridge.exe" start= auto
sc.exe description ZplPrintBridge "ZPL Print Bridge: raw TCP 9100 -> local printer"
sc.exe failure ZplPrintBridge reset= 86400 actions= restart/5000/restart/10000/restart/30000
sc.exe start ZplPrintBridge
```

> **Важно.** В `sc.exe` после `binPath=`, `start=`, `obj=`, `password=` **обязателен пробел**. `binPath="..."` — не сработает, `binPath= "..."` — сработает.

### Удаление

```powershell
.\uninstall-service.bat
```

Или вручную:

```powershell
Stop-Service ZplPrintBridge
sc.exe delete ZplPrintBridge
Remove-Item C:\Services\ZplPrintBridge -Recurse -Force
```

---

## Использование

### Отправка задания печати

С любого клиента в сети откройте TCP-соединение к `IP-сервера:9100` и отправьте байты метки, затем закройте соединение.

Пример на PowerShell:

```powershell
$zpl = @"
^XA
^FO50,50^A0N,40,40^FDOrder 12345^FS
^FO50,120^BY3^BCN,100,Y,N,N^FD123456789012^FS
^XZ
"@
$client = [System.Net.Sockets.TcpClient]::new("10.0.10.38", 9100)
$bytes = [Text.Encoding]::ASCII.GetBytes($zpl)
$client.GetStream().Write($bytes, 0, $bytes.Length)
$client.Close()
```

Пример на C#:

```csharp
using (var client = new TcpClient("10.0.10.38", 9100))
using (var stream = client.GetStream())
{
    var zpl = "^XA^FO50,50^FDHello^FS^XZ";
    var bytes = Encoding.ASCII.GetBytes(zpl);
    stream.Write(bytes, 0, bytes.Length);
}
```

Пример на Python:

```python
import socket
zpl = b"^XA^FO50,50^FDHello^FS^XZ"
with socket.create_connection(("10.0.10.38", 9100)) as s:
    s.sendall(zpl)
```

### Как понять, что задание принято

Если в конфиге `SendAck=true`, после отправки данных служба вернёт строку `OK\n` и закроет соединение. Если `SendAck=false` — просто закроет соединение, без ответа.

---

## Логирование

Служба пишет лог в файл, указанный в `LogFile` (по умолчанию — `ZplPrintBridge.log` рядом с exe).

Формат строк:

```
[2026-09-29 12:34:56] === ZPL Print Bridge starting ===
[2026-09-29 12:34:56] Available printers (3):
[2026-09-29 12:34:56]   - ZDesigner GK420t
[2026-09-29 12:34:56] Resolved printer: ZDesigner GK420t
[2026-09-29 12:34:56] Listening on 0.0.0.0:9100
[2026-09-29 12:35:01] Sent 87 bytes to 'ZDesigner GK420t' (client 10.0.10.115:52341)
[2026-09-29 12:40:12] Printer 'ZDesigner GK420t' not found (1801). Re-resolving by patterns.
[2026-09-29 12:40:12] Found new printer: 'ZDesigner GK420t (Copy 1)'. Retrying print.
```

Просмотр в реальном времени:

```powershell
Get-Content C:\Services\ZplPrintBridge\ZplPrintBridge.log -Wait -Tail 50
```

Просмотр через Event Log (события старта/остановки, которые пишет `ServiceBase`):

```powershell
Get-EventLog -LogName Application -Source ZplPrintBridge -Newest 20
```

---

## Устранение неполадок

### `TcpTestSucceeded: False` при проверке порта

```
PS> Test-NetConnection 10.0.10.38 -Port 9100
TcpTestSucceeded : False
```

Возможные причины:

- Служба не запущена: проверьте командой `Get-Service ZplPrintBridge`.
- Порт занят другим процессом: `netstat -ano | findstr :9100`.
- Брандмауэр блокирует входящие:

```powershell
New-NetFirewallRule -DisplayName "ZPL Bridge" -Direction Inbound -LocalPort 9100 -Protocol TCP -Action Allow
```

### `OpenPrinter('...') failed` с кодом 1801

Принтер с таким именем не найден в контексте службы. Способы решения:

**1. Проверьте точное имя.** Откройте PowerShell от админа и выполните:

```powershell
Get-Printer | Select-Object Name, DriverName
```

Скопируйте точное значение `Name`.

**2. Проверьте, что служба видит принтеры.** В логе при старте выводится список — если он пуст, значит служба (в сеансе 0) не имеет доступа к пользовательским принтерам. Тогда запустите службу от пользователя, у которого принтер доступен:

```powershell
sc.exe config ZplPrintBridge obj= "DOMAIN\user" password= "P@ssw0rd"
Restart-Service ZplPrintBridge
```

**3. Используйте UNC-путь** для сетевого принтера:

```xml
<add key="PrinterName" value="\\PRINT-SERVER\ZDesigner GK420t" />
```

### `FileLoadException: System.Memory, Version=4.0.2.0`

Это ошибка из мира NuGet-пакетов. В текущей версии проекта её быть не может, потому что NuGet-зависимостей нет. Если вы видите её — вы собрали старую ветку с `Microsoft.Extensions.Hosting`. Обновитесь до текущего кода.

### `ConfigurationManager` не существует в текущем контексте

В `.csproj` не подключена ссылка на сборку. Добавьте:

```xml
<ItemGroup>
  <Reference Include="System.Configuration" />
  <Reference Include="System.ServiceProcess" />
</ItemGroup>
```

### `CS0579: Повторяющийся атрибут AssemblyCompanyAttribute`

В проекте остался старый `Properties\AssemblyInfo.cs`. Удалите его — SDK-style проекты генерируют `AssemblyInfo` автоматически.

### Принтер печатает «сырой» текст вместо этикетки

Скорее всего, вы отправили не ZPL, а что-то другое (или отправили ZPL в принтер, который его не понимает). Проверьте:

- метка начинается с `^XA` и заканчивается `^XZ` (для Zebra);
- принтер действительно эмулирует ZPL (для Zebra — режим ZPL, а не EPL);
- данные идут в кодировке ASCII (мы используем `Encoding.ASCII` в примерах; для кириллицы нужна `^CI28` и UTF-8 — но это уже детали прошивки принтера).

---

## Почему .NET Framework 4.8

Изначально служба была на .NET 9, но переезд на .NET Framework 4.8 — осознанное решение:

| Критерий | .NET 9 | .NET Framework 4.8 |
|---|---|---|
| Размер публикации | ~70 МБ (self-contained) | **~100 КБ** |
| NuGet-зависимости | 30+ пакетов (`Microsoft.Extensions.*`) | **0** |
| Конфликты версий (`System.Memory`, `Unsafe`) | Частые | **Отсутствуют** |
| Совместимость с Windows 7 / Server 2008 R2 | Нет | **Есть** |
| Скорость сборки | Дольше | **Мгновенная** |
| Runtime на целевой машине | Нужно ставить .NET 9 | **Уже есть** на Windows 10 21H2+ |

Для такой утилиты, как мост raw-печати, выигрыш в простоте развёртывания и отсутствие зависимостей важнее любых преимуществ современного рантайма. Если вы хотите версию на .NET 8/9 — она возможна, но потребует binding redirects и увеличит поверхность развёртывания.

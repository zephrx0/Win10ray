# Win10ray

[Русский](#русский) · [English](#english) · [Releases](https://github.com/zephrx0/Win10ray/releases)

## Русский

Минималистичный клиент Xray для Windows 10 и 11. Нативный интерфейс на C# и WPF, светлая и тёмная темы, системный прокси и TUN.

![Win10ray: подключение в тёмной теме](docs/images/connection.png)

### Возможности

- Импорт VLESS, VMess, Trojan и Shadowsocks из буфера обмена.
- Подписки по URL: обычный список ссылок и Base64, обновление по кнопке.
- Поиск и группировка серверов, флаги стран, переименование и TCP-пинг.
- Автоматическое переключение на доступный сервер из той же подписки.
- Системный прокси или TUN для TCP/UDP-трафика приложений.
- DNS over HTTPS и режим «Российские сайты напрямую».
- Скорость загрузки и отдачи, трафик за сессию, журнал ядра.
- Автозапуск, восстановление подключения и работа в трее.
- Проверка версий и обновление клиента из GitHub Releases.
- Русский и английский интерфейс по языку Windows, поддержка масштабирования DPI.

### Установка

Требуется **Windows 10/11 x64**. Для готовой сборки установка .NET не нужна.

1. Скачайте [Win10ray-Setup-1.3.1.exe](https://github.com/zephrx0/Win10ray/releases/download/v1.3.1/Win10ray-Setup-1.3.1.exe).
2. Запустите установщик и выберите папку установки.
3. Запустите `Win10ray.exe`, импортируйте ссылку или подписку и выберите сервер.
4. Выберите режим и нажмите «Подключить».

Сохраняйте папки `xray`, `tun` и `service` рядом с клиентом:

```text
Win10ray/
├── Win10ray.exe
├── updater.exe
├── xray/
│   ├── xray.exe
│   ├── geoip.dat
│   └── geosite.dat
├── tun/
│   ├── sing-box.exe
│   ├── wintun.dll
│   └── libcronet.dll
└── service/
    └── Win10ray.TunService.exe
```

### Подключение

**Системный прокси** включает прокси Windows для текущего пользователя. Приложения, которые игнорируют системные настройки прокси, могут подключаться напрямую.

**TUN** создаёт виртуальный сетевой адаптер. При первом использовании Windows запросит права администратора для установки службы `Win10ray.Tun`. После установки обычные подключения выполняются без повторного UAC. Обновление или удаление самой службы может потребовать нового запроса. Окно клиента работает без прав администратора.

DNS по умолчанию: `1.1.1.1`. DNS over HTTPS и исключения для российских сайтов настраиваются в разделе «Маршрутизация». Режим обхода использует `geosite:category-ru` и `geoip:ru`.

Крестик по умолчанию скрывает окно в трей. Для завершения работы используйте «Выход» в клиенте или меню трея.

### Обновления

Начиная с **1.3.1**, клиент обновляется через EXE-установщики GitHub Releases. Пользователям 1.3.0 нужно один раз установить 1.3.1 вручную: старый updater поддерживал только ZIP.

В разделе «Общие» доступны проверка при запуске, «Проверить обновления» и «Обновить клиент». Клиент ищет стабильный релиз с тегом `vX.Y.Z` и файлом `Win10ray-Setup-X.Y.Z.exe`. Если в релизе также есть ZIP, установщик имеет приоритет.

Перед запуском проверяются SHA-256 из [GitHub Releases API](https://docs.github.com/en/rest/releases/assets), название и версия установщика. Затем клиент корректно завершает соединение, а отдельный `updater.exe` запускает Inno Setup с текущей папкой установки. После успешной установки клиент запускается снова без прав администратора. Профили и настройки в AppData сохраняются.

Для установки в Program Files Windows может запросить UAC. Отмена или ошибка установщика отображаются отдельно. Автоматическая перезагрузка Windows запрещена; если установщик сообщает, что она нужна, updater показывает сообщение и ждёт действий пользователя. Журнал установки сохраняется в `%LocalAppData%\Win10ray\Updates\<идентификатор>\setup.log`.

`updater.exe` можно запустить вручную рядом с `Win10ray.exe`. В этом случае для установки завершите клиент через «Выход» в меню трея. Подписки, HWID и токены авторизации в запросах обновлений не передаются.

### Данные и приватность

Настройки, серверы и подписки хранятся локально в `%AppData%\Win10ray`. Папка с программой не содержит ваши настройки. Ключи серверов сохраняются в JSON в открытом виде; доступ к каталогу ограничен текущим пользователем Windows. Не публикуйте эту папку.

Параметр «Отправлять HWID» включён по умолчанию. При запросе подписки сервер получает хеш идентификатора устройства, сведения о Windows и название клиента. Параметр можно выключить в настройках. Ссылки подписок и ключи серверов не записываются в журнал.

Внутренние адреса клиента: SOCKS5 `127.0.0.1:10808`, HTTP `127.0.0.1:10809`, статистика `127.0.0.1:10085`. Для подключения эти порты должны быть свободны.

### Сборка из исходников

Нужны Windows x64, [.NET SDK 8](https://dotnet.microsoft.com/download/dotnet/8.0), Git и PowerShell.

```powershell
git clone https://github.com/zephrx0/Win10ray.git
cd Win10ray
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Prepare-Cores.ps1
dotnet build
dotnet run --project Win10ray.csproj
```

Скрипт загружает официальные Xray-core `v26.10.21` и sing-box `v1.14.4`, проверяет SHA-256 архивов и TUN-компонентов. Бинарные файлы ядер не хранятся в Git. Служба TUN собирается автоматически вместе с клиентом.

Публикация автономной сборки `win-x64`:

```powershell
dotnet publish -c Release -p:PublishProfile=Windows
```

Результат: `artifacts/Win10ray-1.3.1`. Команда `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Publish-Release.ps1` собирает клиент и создаёт `artifacts/Win10ray-Setup-1.3.1.exe` с контрольной суммой. Для неё дополнительно нужен [Inno Setup 6](https://jrsoftware.org/isinfo.php); исходник установщика: `installer/Win10ray.iss`.

### Проверки

```powershell
dotnet run --project tests/XrayMetro.SmokeTests -- --xray xray/xray.exe --sing-box tun/sing-box.exe --tun-helper bin/Debug/net8.0-windows/service/Win10ray.TunService.exe --ui --culture ru-RU
dotnet run --project tests/XrayMetro.SmokeTests -- --ui --culture en-US
```

Проверки охватывают импорт, конфигурации, подписки, переключение серверов, статистику, завершение процессов, IPC службы и интерфейс. Для проверки повторного создания настоящего Wintun-адаптера выполните из PowerShell администратора:

```powershell
powershell -NoProfile -File tests/Invoke-TunAdapterSmoke.ps1
```

Этот тест не переключает системные маршруты или DNS.

Для проверки настоящего EXE-установщика: `powershell -NoProfile -ExecutionPolicy Bypass -File tests/Invoke-InstallerSmoke.ps1`. Тест собирает отдельные установщики без регистрации приложения и ярлыков, устанавливает клиент в временный каталог с пробелами и кириллицей, проверяет версию, сохранение данных и отказ установки. Рабочая установка не меняется.

### Компоненты

- [Xray-core](https://github.com/XTLS/Xray-core), лицензия [MPL-2.0](xray/LICENSE).
- [sing-box](https://github.com/SagerNet/sing-box), отдельный процесс TUN, [GPL-3.0-or-later](tun/COPYING); дополнительные условия: [LICENSE](tun/LICENSE).
- [Wintun](https://www.wintun.net/), [лицензия](tun/LICENSE-Wintun).
- Флаги стран: [Flagpedia / FlagCDN](https://flagpedia.net/download/api), [источник](Assets/Flags/SOURCE.txt).

## English

A minimal Xray client for Windows 10 and 11. Native C# and WPF interface, light and dark themes, system proxy and TUN modes.

![Win10ray connection screen in dark mode](docs/images/connection.png)

### Features

- Import VLESS, VMess, Trojan and Shadowsocks links from the clipboard.
- URL subscriptions: plain text and Base64 lists, with manual refresh.
- Server search and groups, country flags, renaming and TCP latency checks.
- Automatic failover to an available server from the same subscription.
- System proxy or TUN for application TCP/UDP traffic.
- DNS over HTTPS and direct routing for Russian websites.
- Upload and download speed, session traffic and live core logs.
- Start with Windows, restore connections and minimize to the system tray.
- Check for client updates and install them from GitHub Releases.
- Russian or English interface based on the Windows language, with DPI scaling support.

### Installation

Requires **Windows 10/11 x64**. Published builds do not require a separate .NET installation.

1. Download [Win10ray-Setup-1.3.1.exe](https://github.com/zephrx0/Win10ray/releases/download/v1.3.1/Win10ray-Setup-1.3.1.exe).
2. Run the installer and choose an installation folder.
3. Run `Win10ray.exe`, import a link or subscription, and select a server.
4. Choose a connection mode and click **Connect**.

Keep the `xray`, `tun` and `service` folders next to `Win10ray.exe`. Xray needs `xray.exe`, `geoip.dat` and `geosite.dat` in `xray/`; TUN needs `sing-box.exe`, `wintun.dll` and `libcronet.dll` in `tun/`. The helper executable belongs in `service/Win10ray.TunService.exe`.

### Connection modes

**System proxy** changes the current user's Windows proxy settings. Applications that ignore these settings may connect directly.

**TUN** creates a virtual network adapter. On first use, Windows requests administrator permission to install the `Win10ray.Tun` service. Once installed, normal connections do not require another UAC prompt. Updating or uninstalling the service itself may require elevation again. The main client runs without administrator privileges.

The default DNS server is `1.1.1.1`. Configure DNS over HTTPS and direct routing for Russian websites on the **Routing** page. Direct routing uses `geosite:category-ru` and `geoip:ru`.

Closing the window hides it in the system tray by default. Use **Exit** in the client or the tray menu to quit.

### Updates

Starting with **1.3.1**, the client updates through EXE installers from GitHub Releases. Users of 1.3.0 need to install 1.3.1 manually once because the old updater only supported ZIP packages.

Under **General**, enable startup checks or use **Check for updates** and **Update client**. The client looks for a stable release tagged `vX.Y.Z` with `Win10ray-Setup-X.Y.Z.exe`. When both EXE and ZIP packages exist, the installer is preferred.

Before execution, the updater verifies the SHA-256 provided by the [GitHub Releases API](https://docs.github.com/en/rest/releases/assets), the installer identity and its version. The client then shuts down the connection cleanly. A separate `updater.exe` runs Inno Setup with the current installation directory and restarts the client without administrator privileges after success. Profiles and settings in AppData are preserved.

Updating an installation in Program Files may trigger UAC. Cancellation and installer errors are reported separately. Automatic Windows restarts are disabled; if a restart is required, the updater displays a message and waits for the user. Installation logs are saved to `%LocalAppData%\Win10ray\Updates\<session>\setup.log`.

You can also run `updater.exe` manually next to `Win10ray.exe`. For manual installation, quit the client using **Exit** in the tray menu. Update requests do not include subscriptions, HWID or authentication tokens.

### Data and privacy

Settings, servers and subscriptions are stored locally in `%AppData%\Win10ray`. The application folder does not contain your settings. Server credentials are stored as plain JSON; directory access is restricted to the current Windows user. Do not publish this data folder.

**Send HWID** is enabled by default. Subscription requests send a hashed device identifier, Windows information and the client name to the subscription server. You can disable this in settings. Subscription links and server credentials are excluded from logs.

Internal listeners: SOCKS5 `127.0.0.1:10808`, HTTP `127.0.0.1:10809`, statistics `127.0.0.1:10085`. These ports must be available.

### Build from source

Requires Windows x64, [.NET SDK 8](https://dotnet.microsoft.com/download/dotnet/8.0), Git and PowerShell.

```powershell
git clone https://github.com/zephrx0/Win10ray.git
cd Win10ray
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Prepare-Cores.ps1
dotnet build
dotnet run --project Win10ray.csproj
```

The setup script downloads official Xray-core `v26.10.21` and sing-box `v1.14.4` archives, verifies their SHA-256 checksums and checks the TUN components. Core binaries are excluded from Git. Building the client also builds its TUN service.

Publish a self-contained `win-x64` build:

```powershell
dotnet publish -c Release -p:PublishProfile=Windows
```

Output: `artifacts/Win10ray-1.3.1`. Run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Publish-Release.ps1` to build `artifacts/Win10ray-Setup-1.3.1.exe` with a checksum. This also requires [Inno Setup 6](https://jrsoftware.org/isinfo.php); the installer source is `installer/Win10ray.iss`.

### Tests

```powershell
dotnet run --project tests/XrayMetro.SmokeTests -- --xray xray/xray.exe --sing-box tun/sing-box.exe --tun-helper bin/Debug/net8.0-windows/service/Win10ray.TunService.exe --ui --culture ru-RU
dotnet run --project tests/XrayMetro.SmokeTests -- --ui --culture en-US
```

Checks cover imports, core configurations, subscriptions, failover, statistics, process cleanup, service IPC and the interface. To test repeated creation of a real Wintun adapter, run this command in an administrator PowerShell session:

```powershell
powershell -NoProfile -File tests/Invoke-TunAdapterSmoke.ps1
```

This adapter test does not switch system routes or DNS.

To exercise a real EXE installer, run `powershell -NoProfile -ExecutionPolicy Bypass -File tests/Invoke-InstallerSmoke.ps1`. The test builds isolated installers without registration or shortcuts, installs into a temporary path containing spaces and Cyrillic characters, and verifies the version, preserved data and installer failure handling. It does not change the working installation.

### Third-party components

- [Xray-core](https://github.com/XTLS/Xray-core), [MPL-2.0](xray/LICENSE).
- [sing-box](https://github.com/SagerNet/sing-box), a separate TUN process, [GPL-3.0-or-later](tun/COPYING); additional terms: [LICENSE](tun/LICENSE).
- [Wintun](https://www.wintun.net/), [license](tun/LICENSE-Wintun).
- Country flags: [Flagpedia / FlagCDN](https://flagpedia.net/download/api), [source](Assets/Flags/SOURCE.txt).

# Сборка и проверка / Build and verification

## Запуск из исходников

Windows 10/11 x64, .NET SDK 8, PowerShell. Из корня репозитория:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Prepare-Cores.ps1
dotnet build
dotnet run --project Win10ray.csproj
```

Рядом с `Win10ray.exe` нужны каталоги `xray` (`xray.exe`, `geoip.dat`, `geosite.dat`), `tun` (`sing-box.exe`, `wintun.dll`, `libcronet.dll`) и `service`, а также `updater.exe`. Подготовка ядер скачивает официальные архивы Xray 26.10.21 и sing-box 1.14.4, проверяет SHA-256. Бинарные файлы исключены из Git.

## Установщик

Дополнительно установите [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Publish-Release.ps1
# Экспериментальная сборка:
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Publish-Release.ps1 -Version 1.4.4-experimental.1
```

Результат: `artifacts/Win10ray-<версия>` и `artifacts/Win10ray-Setup-<версия>.exe`. Self-contained win-x64, без установки .NET у пользователя. Скрипт создаёт SHA-256 рядом с установщиком; публикация на GitHub выполняется отдельно.

`main` содержит стабильные изменения, `experimental` служит для предварительных сборок. Экспериментальные релизы имеют тег `vX.Y.Z-experimental.N` и отметку GitHub **Pre-release**. Апдейтер проверяет имя установщика, размер, SHA-256 GitHub и версию EXE. Стабильный канал не предлагает предварительные релизы. Возврат с экспериментальной версии допускает более низкую стабильную версию начиная с 1.4.0; старые клиенты не понимают зашифрованный профиль.

## Тесты

```powershell
dotnet run --project tests/XrayMetro.SmokeTests -- --xray xray/xray.exe --sing-box tun/sing-box.exe --tun-helper bin/Debug/net8.0-windows/service/Win10ray.TunService.exe --ui --culture ru-RU
dotnet run --project tests/XrayMetro.SmokeTests -- --ui --culture en-US
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Invoke-InstallerSmoke.ps1
```

Проверяются DPAPI и миграция, ссылки и QR, подписки, конфигурации обоих ядер, правила процессов, каналы обновлений, жизненный цикл процессов, интерфейс и изолированный установщик. Тест установщика не меняет рабочую установку. Проверка конфигураций TUN не меняет маршруты компьютера.

## Данные и ограничения

- Экспериментальный `settings.json` в `%AppData%\Win10ray\Experimental` содержит зашифрованный DPAPI профиль: серверы, подписки и настройки. При первом запуске копируется стабильный профиль; после этого хранилища независимы. Используется CurrentUser и постоянная энтропия приложения. Ключей в исходниках нет.
- При миграции создаётся **зашифрованная** `.bak` на одну версию. При следующей версии она удаляется. Файлы записываются через временный файл и атомарную замену. Физическое стирание ранее записанных данных на SSD не гарантируется.
- Если профиль нельзя расшифровать, клиент предлагает начать заново и сохраняет исходный файл отдельно. Перенос зашифрованного профиля на другой аккаунт Windows не поддерживается.
- Пока ядро работает, его временный `runtime/config.json` доступен в открытом виде только текущему пользователю. Xray должен прочитать конфигурацию; при остановке файл удаляется. Токен сеанса TUN и HWID не записываются в профиль или журнал.
- Отправка HWID включена по умолчанию: сервер подписки получает хеш идентификатора устройства, сведения о Windows и название/версию Win10ray. Перенаправления на другой сервер не получают HWID. GitHub-апдейтер не отправляет HWID и данные подписки.
- Автообновление подписок включено, интервал 12 часов. Доступны 1/3/6/12/24 часа, обновление при запуске и появлении сети. Автоматические попытки ограничены 15 минутами, ошибки повторяются с увеличением интервала. Ручное обновление доступно без этого ограничения. «Через подключение» использует локальный прокси работающего клиента; без подключения запрос идёт напрямую.
- QR-код даёт доступ к подписке или серверу. Он не кэшируется, сохранение PNG происходит только по команде пользователя.
- В TUN служебные процессы и DNS-перехват имеют обязательные правила для предотвращения петель. Выбранные приложения идут напрямую, остальные через Xray с учётом блокировщика и обхода российских сайтов. «Применить» переподключает VPN с новым списком приложений. Режим «Всё напрямую» удалён; старые настройки этого режима сбрасываются.
- Windows может отправлять DNS от имени общей службы, а не исходного приложения. В этом случае DNS-исключение по имени приложения неприменимо, общий DNS-блокировщик сохраняется. Встроенный DoH браузера также не позволяет фильтровать его DNS-запросы; соединения с известным доменом проверяются маршрутизатором.
- Внутренние порты: 10808, 10809, 10085 и 10810 при наличии правила Proxy для процесса. Они слушают только 127.0.0.1. Мост OpenVPN выбирает свободные локальные порты динамически. Экспериментальная служба TUN размещается отдельно в `%ProgramFiles%\Win10ray.TunService.Experimental`; при первой установке требуется однократное подтверждение администратора.
- OpenVPN работает через встроенный транспорт sing-box, с конфигурацией через stdin. Приватные ключи конфигурации ядра не записываются во временный файл. [Импорт и ограничения](OPENVPN.md).
- Счётчики получают трафик из Xray, включая явные правила Proxy. Прямые исключения, которые sing-box обрабатывает без Xray, в эти счётчики не входят.

## Компоненты / Components

- [Xray-core](https://github.com/XTLS/Xray-core), [MPL-2.0](../xray/LICENSE).
- [sing-box](https://github.com/SagerNet/sing-box), отдельный процесс, [GPL-3.0-or-later](../tun/COPYING), [дополнительные условия](../tun/LICENSE).
- [Wintun](https://www.wintun.net/), [лицензия](../tun/LICENSE-Wintun).
- [QRCoder](https://github.com/codebude/QRCoder), [MIT](licenses/QRCoder.txt).
- Списки доменов [v2fly/domain-list-community](https://github.com/v2fly/domain-list-community), [MIT](../Assets/Routing/LICENSE). Встроенный снимок тегов `category-ads-all` и `category-ads-and-telemetry` извлечён из поставляемого `geosite.dat`, его SHA-256 указан в `Assets/Routing/ads.json`. Xray использует те же теги, sing-box использует этот снимок. Для обновления снимка: `dotnet run --project tests/GeoSiteExport` после подготовки проверенных ядер.
- Флаги [Flagpedia / FlagCDN](https://flagpedia.net/download/api), [источник](../Assets/Flags/SOURCE.txt).

## English notes

Build with .NET SDK 8 on Windows x64. Run `Prepare-Cores.ps1`, then `dotnet build` or `dotnet publish -c Release -p:PublishProfile=Windows`. `Publish-Release.ps1` requires Inno Setup 6 and produces a self-contained EXE installer. Use `-Version X.Y.Z-experimental.N` on the `experimental` branch and publish it as a GitHub pre-release.

Profiles are encrypted with DPAPI CurrentUser. Migration keeps an encrypted backup for one version; writes are atomic. Resetting an unreadable profile requires confirmation and preserves the original. Runtime Xray configuration is temporarily plaintext with a private user ACL and is removed on stop. Physical erasure of old SSD contents cannot be guaranteed.

Subscription refresh runs every 12 hours by default and on startup/network recovery, with a 15-minute automatic retry minimum and exponential backoff. Manual refresh bypasses this limit. Application rules require TUN and override normal routing filters. Windows shared DNS service attribution can prevent per-application DNS exceptions; the global DNS block rule then remains active. QR links grant access and must be kept private. See the component sources and licenses above.

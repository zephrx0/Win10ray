# Win10ray

> Экспериментальная ветка. Возможны ошибки и прерывания подключения. [Стабильная версия](https://github.com/zephrx0/Win10ray/releases/latest).
> Experimental branch. Bugs and connection interruptions are possible.

[![Release](https://img.shields.io/github/v/release/zephrx0/Win10ray?label=stable)](https://github.com/zephrx0/Win10ray/releases/latest)
[![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D7)](https://github.com/zephrx0/Win10ray/releases/latest)
[![.NET](https://img.shields.io/badge/.NET-8-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)

Клиент Xray для Windows с интерфейсом в стиле Windows 10. C#, WPF, без веб-компонентов.

![Win10ray](docs/images/connection.png)

## Возможности

- VLESS, VMess, Trojan, Shadowsocks. Подписки, QR-коды и флаги серверов. Узлы с одинаковым названием в одной подписке объединяются.
- [JSON-подписки Xray](docs/JSON.md): автоматический выбор формата; серверы и балансировщики провайдера сохраняются одной записью.
- OpenVPN в экспериментальной версии: импорт `.ovpn`, UDP/TCP, сертификаты и логин/пароль.
- Системный прокси и TUN, обход VPN для выбранных приложений, блокировка рекламы и трекеров, DNS over HTTPS.
- Трей, автоподключение, переключение недоступных серверов, тёмная и светлая темы, русский и английский интерфейс.

## Скачать

[Экспериментальная версия с OpenVPN](https://github.com/zephrx0/Win10ray/releases/tag/v1.4.5-experimental.1) · [Стабильная версия](https://github.com/zephrx0/Win10ray/releases/latest) · [Все релизы](https://github.com/zephrx0/Win10ray/releases)

Запустите `Win10ray-Setup-*.exe`, импортируйте ссылку или подписку и нажмите «Подключить». Для первого включения TUN подтвердите запрос администратора.

Канал обновлений выбирается в **Общие → Обновления** и в `updater.exe`. Экспериментальные версии могут содержать ошибки, устанавливаются после предупреждения. Для возврата выберите стабильный канал.

Для OpenVPN: **Серверы → Файл .ovpn** или перетащите файл в окно. [Поддерживаемые профили и ограничения](docs/OPENVPN.md).

## Данные и приватность

Экспериментальный профиль в `%AppData%\Win10ray\Experimental` зашифрован Windows DPAPI. При первом запуске копируются настройки стабильной версии; дальше профили независимы. HWID включён по умолчанию для подписок и отключается в настройках. QR-коды создаются локально и содержат секретные ссылки. [Подробности](docs/BUILD.md#данные-и-ограничения).

## Сборка

Windows x64, .NET SDK 8. Ядра загружаются отдельно с проверкой SHA-256.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Prepare-Cores.ps1
dotnet build
dotnet publish -c Release -p:PublishProfile=Windows
```

[Установщик, тесты и компоненты](docs/BUILD.md).

---

## English

A native Xray client with a Windows 10 style interface. Built with C# and WPF.

- VLESS, VMess, Trojan and Shadowsocks; subscriptions, QR codes and server flags. Nodes with the same name within a subscription are grouped.
- [Xray JSON subscriptions](docs/JSON.md) select the format automatically and retain provider servers and balancers in one entry.
- Experimental OpenVPN support: `.ovpn` import, UDP/TCP, certificates and username/password authentication.
- System proxy and TUN, VPN bypass for selected apps, ad and tracker blocking, DNS over HTTPS.
- Tray, automatic connection and server failover, light/dark themes, English/Russian interface.

### Download

[Experimental OpenVPN build](https://github.com/zephrx0/Win10ray/releases/tag/v1.4.5-experimental.1) · [Stable release](https://github.com/zephrx0/Win10ray/releases/latest) · [All releases](https://github.com/zephrx0/Win10ray/releases)

Run `Win10ray-Setup-*.exe`, import a link or subscription and click **Connect**. TUN requires administrator approval on first use.

For OpenVPN, choose **Servers → .ovpn file** or drag the file into the window. [Supported profiles and limitations](docs/OPENVPN.md).

Choose the update channel in **General → Updates** or `updater.exe`. Experimental builds may contain bugs and require confirmation. Select Stable to return to a stable release.

The experimental profile uses Windows DPAPI in `%AppData%\Win10ray\Experimental`. Stable settings are copied on first launch, then kept separate. HWID is enabled by default for subscription requests and can be disabled. QR codes are generated locally and contain private links.

### Build

Windows x64 and .NET SDK 8 are required. Run the three commands above. [Installer, tests and component licenses](docs/BUILD.md).

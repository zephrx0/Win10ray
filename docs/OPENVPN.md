# OpenVPN (experimental)

Поддержка доступна в ветке `experimental`, начиная с `1.4.2-experimental.2`. В стабильной версии её нет.

## Подключение

1. Откройте **Серверы → Файл .ovpn** или перетащите файл в окно. Можно выбрать до 32 файлов.
2. Выберите импортированный сервер, режим **Системный прокси** или **TUN** и нажмите **Подключить**.
3. Если профиль требует логин и пароль, клиент запросит их. Сохранение необязательно; сохранённые данные защищены DPAPI. Изменить их можно через контекстное меню сервера.

Встроенные сертификаты и ключи поддерживаются. Если `.ovpn` ссылается на PEM-файлы, положите их рядом с профилем либо в его подпапку. При импорте содержимое включается в зашифрованный профиль; ссылки за пределы выбранной папки и переходы через junction/symlink не принимаются.

## Ссылки

Кнопка **Импорт** принимает содержимое `.ovpn` из буфера обмена, прямую HTTPS-ссылку на профиль и стандартный формат:

```text
openvpn://import-profile/https://vpn.example/profile.ovpn
```

URL должен отдавать профиль без браузерной страницы входа. Также поддерживается собственный формат Win10ray `ovpn://<Base64-UTF8-профиль>#Имя`. Это не универсальная схема OpenVPN. Неподдерживаемые варианты ссылок не преобразуются в фиктивный профиль.

Профиль можно сохранить в `.ovpn` через контекстное меню. Экспорт содержит ключи в открытом виде, поэтому перед сохранением показывается предупреждение. Большие профили не помещаются в QR: используйте файл либо HTTPS-ссылку.

## Ядро и маршрутизация

Используется реализация OpenVPN в поставляемом **sing-box 1.14.4** (`with_openvpn`), отдельным скрытым процессом с внутренним сетевым стеком. Официальный `openvpn.exe` не запускается, установка TAP-драйвера не требуется. [Документация ядра](https://sing-box.sagernet.org/configuration/endpoint/openvpn-client/).

Xray направляет трафик в локальный SOCKS-мост OpenVPN. Поэтому сохраняются счётчики, блокировка рекламы, российские сайты напрямую и правила приложений в TUN. Для клиентских DNS-запросов используется `1.1.1.1` или выбранный DoH через OpenVPN. Адреса VPN-серверов разрешаются до включения TUN. Маршруты/DNS, заданные в профиле или отправленные сервером, не заменяют правила Win10ray.

Готовность проверяется через закрытый локальный API ядра, после завершения TLS-подключения. При отмене, выходе и падении клиента процесс останавливается Windows Job Object. Конфигурация OpenVPN передаётся через стандартный ввод и не записывается на диск. TCP-пинг не применяется к UDP-профилям.

## Поддержка и ограничения

Поддерживаются TLS-клиентские профили TUN по UDP/TCP, несколько `remote`, CA/клиентские PEM-сертификаты и ключи, SHA-256 fingerprint, `tls-auth`, `tls-crypt`, `tls-crypt-v2`, логин/пароль, основные параметры шифрования, MTU и времени соединения. Скрипты, плагины, вложенные конфиги и неизвестные директивы отклоняются.

В этой версии нет TAP/L2, static-key режима без TLS, PKCS#12, зашифрованных приватных PEM-ключей, `<connection>`-блоков и интерактивных MFA/SAML/web-auth flow. Для неподдерживаемого профиля выводится ошибка; при необходимости экспортируйте у провайдера обычный клиентский TUN-профиль с PEM-материалами.

Экспериментальные настройки находятся в `%AppData%\Win10ray\Experimental`. При первом запуске создаётся копия стабильного профиля. Стабильные настройки после этого не изменяются. Экспериментальная служба TUN имеет отдельное имя `Win10ray.Tun.Experimental` и каталог `%ProgramFiles%\Win10ray.TunService.Experimental`; при первом включении подтвердите UAC. При возврате на стабильный канал откроется исходный стабильный профиль.

## Проверка

```powershell
dotnet run --project tests/XrayMetro.SmokeTests -- --openvpn-only tun/sing-box.exe
```

Тест создаёт временные CA и клиентские/серверные сертификаты, запускает локальные OpenVPN TLS-серверы UDP/TCP без системных интерфейсов, проверяет `tls-crypt`, пароль, DNS и HTTP через Xray, счётчики, отмену и очистку. Системные маршруты не меняются. Конкретный профиль провайдера без доступа к его серверу этим тестом не проверяется.

## English

OpenVPN is available only in experimental builds. Import an `.ovpn` using **Servers → .ovpn file** or drag and drop. Choose System proxy or TUN and connect. Username/password prompts support optional DPAPI-protected storage. Relative PEM files inside the selected folder are embedded during import.

Clipboard import accepts raw profiles, direct HTTPS download URLs, official `openvpn://import-profile/https://…` URLs and Win10ray-specific Base64 `ovpn://…` links. Browser-based Access Server login and MFA/SAML are not supported. Large profiles may exceed QR capacity; export an `.ovpn` or use an HTTPS profile URL.

The engine is sing-box 1.14.4's native OpenVPN implementation, running as a separate userspace process behind Xray. It is not the official `openvpn.exe`. No TAP installation is needed. Xray routing and statistics remain available; destination DNS goes through OpenVPN, with optional DoH. Provider-pushed routes/DNS do not replace Win10ray policy.

TLS TUN profiles over UDP/TCP, PEM certificates/keys, SHA-256 fingerprints, tls-auth/tls-crypt/tls-crypt-v2 and username/password are supported. TAP, static-key mode, PKCS#12, encrypted PEM keys, connection blocks, executable scripts/plugins and interactive web authentication are not supported in this build.

Experimental profiles and the privileged TUN service are separate from stable ones. First launch copies existing stable settings. Switching back restores the original stable profile. Local integration tests cover actual TLS, encrypted data/DNS through Xray, statistics and cleanup without changing system routes; an individual provider profile still needs a live test.

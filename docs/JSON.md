# JSON-профили Xray

Импортируйте подписку, вставьте JSON через «Импорт», выберите «Файл JSON» или перетащите `.json` в окно. Поддерживаются отдельный объект Xray, массив таких объектов и JSON в Base64.

Один объект остаётся одной записью в списке. Название берётся из `remarks`, а серверы внутри не превращаются в отдельные плитки. Сохраняются `routing`, `balancers`, стратегии random/roundRobin/leastPing/leastLoad и `observatory`/`burstObservatory`. Для leastPing/leastLoad профиль должен содержать наблюдатель.

Клиент назначает собственные локальные порты, API статистики и настройки журнала. Его блокировщик и обход российских сайтов применяются перед правилами профиля. DNS профиля сохраняется; включение DoH в клиенте заменяет список DNS-серверов. Исключения приложений в TUN продолжают работать.

Поддерживаются исходящие VLESS, VMess, Trojan, Shadowsocks, SOCKS, HTTP, Freedom, Blackhole, DNS и Loopback. Внешние файлы сертификатов/ключей и геоданных, Lua, вебхуки и Browser Dialer не поддерживаются. Используйте встроенные PEM-данные и стандартные `geoip.dat`/`geosite.dat`.

JSON-профили требуют Win10ray 1.4.4 или новее и хранятся с Windows DPAPI. «Сохранить JSON» создаёт незашифрованный файл с ключами доступа. Большой профиль может не поместиться в QR-код.

Начиная с 1.4.5, после получения списка ссылок Win10ray также проверяет вариант подписки `/json`. Если панель его поддерживает, загружается JSON с балансировщиками. Иначе сохраняется исходный список. Дополнительный запрос ограничен пятью секундами; адрес подписки, HWID и имя клиента не меняются. Явно заданные форматы и файловые URL не изменяются. В контекстном меню подписки можно отключить «Предпочитать JSON Xray».

Если провайдер использует другой адрес JSON или отдельные правила для приложений, потребуется его URL Xray JSON либо правило панели для Win10ray. Частные форматы других приложений не имитируются.

## English

Import a subscription, paste JSON using Import, choose JSON file or drop a `.json` file into the window. Single Xray configuration objects, arrays and Base64-encoded JSON are supported.

Each object remains one list entry named by `remarks`. Its outbounds, routing, balancers, random/roundRobin/leastPing/leastLoad strategies and observatories are retained. leastPing/leastLoad require an observatory in the profile.

Win10ray owns local listeners, statistics API and logging. Application ad blocking and Russian bypass rules take priority. Profile DNS is retained unless client DoH is enabled. TUN application exclusions remain available.

Supported outbounds: VLESS, VMess, Trojan, Shadowsocks, SOCKS, HTTP, Freedom, Blackhole, DNS and Loopback. External certificate/key/geodata files, Lua, webhooks and Browser Dialer are unsupported. Use inline PEM and bundled geodata.

JSON profiles require Win10ray 1.4.4 or later and are stored with Windows DPAPI. Save JSON exports an unencrypted file containing access credentials; large profiles may exceed QR capacity.

Since 1.4.5, Win10ray also checks the `/json` subscription variant after receiving a link list. Supported JSON retains provider balancers; otherwise the original list is kept. The extra request has a five-second timeout. The stored URL, HWID and client identity remain unchanged. Explicit formats and file URLs are respected. Disable Prefer Xray JSON in the subscription context menu to keep the original response.

Providers with a different JSON endpoint or application-specific rules may require their Xray JSON URL or a panel rule for Win10ray. Proprietary formats from other applications are not emulated.

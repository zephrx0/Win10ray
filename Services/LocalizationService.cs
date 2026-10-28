using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Markup;

namespace XrayMetro.Services;

public static class LocalizationService
{
    public static bool IsRussian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase);
    public static string Pick(string ru, string en) => IsRussian ? ru : en;
    public static string Get(string key) => Labels.TryGetValue(key, out var pair) ? Pick(pair.Ru, pair.En) : key;
    private static readonly Dictionary<string, (string Ru, string En)> Labels = new()
    {
        ["WaitingNetwork"] = ("Ожидание сети", "Waiting for network"),
        ["PreparingTun"] = ("Подготовка службы TUN", "Preparing the TUN service"),
        ["StartupNetworkTimeout"] = ("Не удалось восстановить подключение за 45 секунд. Проверьте сеть и журнал.", "Could not restore the connection within 45 seconds. Check your network and log."),
        ["TunAdapterTimeout"] = ("Адаптер TUN не запустился вовремя. Подробности в журнале.", "The TUN adapter did not start in time. Check the log."),
        ["TunBackendExit"] = ("Процесс TUN завершился при запуске. Подробности в журнале.", "The TUN process exited during startup. Check the log."),
        ["TunPipeClosed"] = ("Служба TUN закрыла канал во время запуска. Повторите подключение; подробности в журнале.", "The TUN service closed its pipe during startup. Retry and check the log."),
        ["TunAccessDenied"] = ("Нет доступа к службе TUN. Проверьте установку службы для текущего пользователя.", "Access to the TUN service was denied. Check the service installation for this user."),
        ["TunXrayUnavailable"] = ("Служба TUN не может подключиться к локальному Xray.", "The TUN service cannot connect to the local Xray core."),
        ["Cancel"] = ("Отмена", "Cancel"),
        ["SendHwid"] = ("Отправлять HWID", "Send HWID"),
        ["SendHwidHint"] = ("Провайдеру подписки при её обновлении", "To the subscription provider when updating"),
        ["AutoSwitchServers"] = ("Переключать недоступные серверы", "Switch unavailable servers"),
        ["NoAvailableServers"] = ("В подписке нет доступных серверов.", "No available servers in this subscription."),
        ["ServerSwitched"] = ("Выбран доступный сервер подписки.", "Switched to an available server in the subscription."),
        ["HwidLimit"] = ("Достигнут лимит устройств подписки.", "The subscription device limit has been reached."),
        ["HwidRequired"] = ("Для этой подписки нужно включить отправку HWID.", "Enable HWID for this subscription."),
        ["ClientVersion"] = ("Версия Win10ray", "Win10ray version"),
        ["CoreVersion"] = ("Версия Xray", "Xray version"),
        ["CoreMissing"] = ("Ядро не найдено", "Core not found"),
        ["About"] = ("О приложении", "About"),
        ["Imported"] = ("Импортированные", "Imported"),
        ["SearchServers"] = ("Найти сервер", "Find a server"),
        ["EditServer"] = ("Изменить сервер", "Edit server"),
        ["NoSearchResults"] = ("Серверы не найдены", "No servers found"),
        ["AddSubscription"] = ("Добавить подписку", "Add subscription"),
        ["Home"] = ("Подключение", "Connection"), ["Servers"] = ("Серверы", "Servers"),
        ["Routing"] = ("Маршрутизация", "Routing"), ["Logs"] = ("Журнал", "Log"), ["Settings"] = ("Настройки", "Settings"),
        ["Connect"] = ("Подключить", "Connect"), ["Disconnect"] = ("Отключить", "Disconnect"),
        ["Connecting"] = ("Подключение", "Connecting"), ["Connected"] = ("Подключено", "Connected"),
        ["Disconnected"] = ("Отключено", "Disconnected"), ["Error"] = ("Ошибка", "Error"),
        ["Wait"] = ("Подождите", "Please wait"), ["SelectServer"] = ("Выбрать сервер", "Choose server"),
        ["Disconnecting"] = ("Отключение", "Disconnecting"), ["General"] = ("Общие", "General"),
        ["Server"] = ("Сервер", "Server"), ["Theme"] = ("Тема", "Theme"), ["Dark"] = ("Тёмная", "Dark"), ["Light"] = ("Светлая", "Light"),
        ["On"] = ("Вкл", "On"), ["Off"] = ("Выкл", "Off"),
        ["CloseToTray"] = ("Закрывать в трей", "Close to tray"),
        ["DohUrl"] = ("Адрес DoH", "DoH endpoint"), ["InvalidDoh"] = ("Укажите корректный HTTPS-адрес DNS-сервера.", "Enter a valid HTTPS DNS endpoint."),
        ["Import"] = ("Импорт", "Import"), ["Paste"] = ("Вставить", "Paste"), ["Ping"] = ("Проверить пинг", "Check latency"),
        ["Rename"] = ("Переименовать", "Rename"), ["Delete"] = ("Удалить", "Delete"), ["Save"] = ("Сохранить", "Save"),
        ["Subscriptions"] = ("Подписки", "Subscriptions"), ["Add"] = ("Добавить", "Add"), ["Update"] = ("Обновить", "Refresh"),
        ["SubscriptionUrl"] = ("URL подписки", "Subscription URL"), ["Copy"] = ("Копировать", "Copy"), ["Clear"] = ("Очистить", "Clear"),
        ["SystemProxy"] = ("Системный прокси", "System proxy"),
        ["Mode"] = ("Режим", "Mode"), ["BypassRussia"] = ("Российские сайты напрямую", "Bypass Russian sites"),
        ["AutoStart"] = ("Запускать с Windows", "Start with Windows"), ["MinimizeToTray"] = ("Сворачивать в трей", "Minimize to tray"),
        ["StartMinimized"] = ("Запускать свёрнутым", "Start minimized"), ["AutoConnect"] = ("Подключаться при запуске", "Connect on startup"),
        ["Appearance"] = ("Оформление", "Appearance"), ["DarkTheme"] = ("Тёмная тема", "Dark theme"),
        ["Download"] = ("Загрузка", "Download"), ["Upload"] = ("Отдача", "Upload"), ["Traffic"] = ("Трафик", "Traffic"),
        ["Minimize"] = ("Свернуть", "Minimize"), ["Maximize"] = ("Развернуть", "Maximize"), ["Close"] = ("Закрыть", "Close"),
        ["Open"] = ("Открыть Win10ray", "Open Win10ray"), ["Exit"] = ("Выход", "Exit"),
        ["Copied"] = ("Скопировано", "Copied"), ["ClipboardBusy"] = ("Буфер обмена занят. Повторите действие.", "Clipboard is busy. Try again."),
        ["NoServers"] = ("Нет серверов", "No servers"), ["Unavailable"] = ("Недоступен", "Unavailable"),
        ["Checking"] = ("Проверка", "Checking"), ["Ms"] = ("мс", "ms"),
        ["OperationFailed"] = ("Не удалось выполнить действие. Проверьте подключение и доступ к файлам.", "Action failed. Check your connection and file permissions."),
        ["TunMissing"] = ("Не найдены компоненты TUN. Используйте полную папку приложения.", "TUN components are missing. Use the complete application folder."),
        ["TunUacCancelled"] = ("Установка TUN отменена.", "TUN setup was cancelled."),
        ["TunInstallFailed"] = ("Не удалось установить службу TUN.", "Could not install the TUN service."),
        ["TunUnavailable"] = ("Служба TUN недоступна.", "TUN service is unavailable."),
        ["TunStartFailed"] = ("Не удалось включить TUN. Проверьте журнал и другие VPN-подключения.", "Could not start TUN. Check the log and other VPN connections."),
        ["TunLost"] = ("Связь со службой TUN потеряна. Подключение завершено.", "Connection to the TUN service was lost. Disconnected."),
        ["TunBusy"] = ("TUN используется другим экземпляром.", "TUN is in use by another instance."),
        ["StartupFailed"] = ("Не удалось запустить Win10ray. Проверьте доступ к AppData и реестру.", "Could not start Win10ray. Check AppData and registry permissions."),
        ["AlreadyRunning"] = ("Win10ray уже запущен. Откройте его из трея.", "Win10ray is already running. Open it from the system tray."),
        ["FatalError"] = ("Win10ray завершает работу после ошибки. Подключение остановлено.", "Win10ray must close after an error. The connection has been stopped.")
    };
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => LocalizationService.Get(key);
}

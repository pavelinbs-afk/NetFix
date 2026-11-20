using System.Diagnostics;
using System.IO;
using System.Net.Http;
using NetFix.Models;

namespace NetFix.Services;

public static class AutoSetupService
{
    private const string ZapretRepo    = "Flowseal/zapret-discord-youtube";
    private const string TgWsProxyRepo = "Flowseal/tg-ws-proxy";

    public static void Run(
        Action<string, string> logCb,
        Action<double> progressCb,
        Action<bool, string> doneCb,
        AppSettings settings)
    {
        Task.Run(async () =>
        {
            try { await RunAsync(logCb, progressCb, doneCb, settings); }
            catch (Exception e) { logCb($"Неожиданная ошибка: {e.Message}", "error"); doneCb(false, "exception"); }
        });
    }

    private static async Task RunAsync(
        Action<string, string> log,
        Action<double> progress,
        Action<bool, string> done,
        AppSettings settings)
    {
        log("Проверяю подключение к интернету…", "info");
        progress(0.05);
        bool hasNet = await DiagnosticsEngine.CheckInternetAsync();
        if (!hasNet)
        {
            log("Нет интернета. Подключитесь и попробуйте снова.", "error");
            done(false, "no_internet");
            return;
        }
        log("Интернет есть", "ok");
        progress(0.10);
        await Task.Delay(200);

        log("Проверяю последние версии инструментов…", "info");
        string zapretVer   = settings.EnableZapret ? await GetLatestGitHubRelease(ZapretRepo) : "";
        string tgWsVer     = settings.EnableTgWsProxy ? await GetLatestGitHubRelease(TgWsProxyRepo) : "";
        progress(0.20);

        bool wantZapret = settings.EnableZapret
            && !string.IsNullOrEmpty(settings.ZapretPath)
            && File.Exists(settings.ZapretPath);
        bool wantTg = settings.EnableTgWsProxy
            && !string.IsNullOrEmpty(settings.TgWsProxyPath)
            && File.Exists(settings.TgWsProxyPath);

        if (settings.EnableZapret)
        {
            if (!wantZapret)
            {
                log($"Нахожу Zapret {(string.IsNullOrEmpty(zapretVer) ? "последнюю версию" : zapretVer)}…", "info");
                log($"Откройте в браузере и скачайте архив для Windows:\nhttps://github.com/{ZapretRepo}/releases/latest", "link");
                log("⏳ После скачивания укажите путь в Настройках → Пути к файлам", "warn");
            }
            else
            {
                log($"Zapret найден: {settings.ZapretPath}", "ok");
                if (await ZapretConfigService.IsZapretUpAsync())
                {
                    log("Zapret уже запущен", "ok");
                }
                else
                {
                    var isServiceBat = Path.GetFileName(settings.ZapretPath)
                        .Equals("service.bat", StringComparison.OrdinalIgnoreCase);
                    if (isServiceBat)
                    {
                        var cache = ZapretConfigService.LoadCache() ?? new ZapretConfigCache();
                        log($"Запускаю Zapret ({cache.CurrentConfig ?? "авто"})…", "info");

                        var (ok, used) = await ZapretConfigService.TryStartBestConfigAsync(
                            settings.ZapretPath,
                            cache,
                            name => log($"Пробую конфиг «{name}»…", "info"));

                        if (ok)
                            log($"Zapret запущен{(string.IsNullOrEmpty(used) ? "" : $" («{used}»)")}", "ok");
                        else
                            log("Не удалось запустить Zapret", "warn");
                    }
                    else
                    {
                        TryLaunch(settings.ZapretPath, log);
                        await WaitForProcessAsync(["winws", "nfqws"], 4000);
                    }
                }
            }
        }
        else
        {
            log("Zapret отключён в настройках", "info");
        }
        progress(0.50);
        await Task.Delay(200);

        if (settings.EnableTgWsProxy)
        {
            if (!wantTg)
            {
                log($"Нахожу tg-ws-proxy {(string.IsNullOrEmpty(tgWsVer) ? "последнюю версию" : tgWsVer)}…", "info");
                log($"Откройте в браузере и скачайте для Windows:\nhttps://github.com/{TgWsProxyRepo}/releases/latest", "link");
                log("⏳ После скачивания укажите путь в Настройках → Пути к файлам", "warn");
            }
            else
            {
                log($"tg-ws-proxy найден: {settings.TgWsProxyPath}", "ok");
                if (IsProcessRunning(["tg-ws-proxy", "tgwsproxy", "tg_ws_proxy"]))
                    log("tg-ws-proxy уже запущен", "ok");
                else
                {
                    TryLaunch(settings.TgWsProxyPath, log);
                    await WaitForProcessAsync(["tg-ws-proxy", "tgwsproxy", "tg_ws_proxy"], 4000);
                }
            }
        }
        else
        {
            log("TgWsProxy отключён в настройках", "info");
        }
        progress(0.75);
        await Task.Delay(200);

        // не пишем «готово», если winws мёртв
        var st = await DiagnosticsEngine.CheckAppStatusAsync();
        bool zapretOk = !wantZapret || st.ZapretRunning;
        bool tgOk = !wantTg || st.TgWsProxyRunning;
        bool allOk = zapretOk && tgOk;

        log("", "spacer");
        progress(1.0);
        await Task.Delay(150);

        if (allOk)
        {
            if (wantTg)
                log("Готово! Telegram должен работать нормально.", "success");
            else if (wantZapret)
                log("Готово! Zapret поднят.", "success");
            else
                log("Готово.", "success");
            done(true, "ok");
        }
        else
        {
            if (!zapretOk) log("Zapret так и не поднялся — проверьте конфиг или вкладку сервисов.", "warn");
            if (!tgOk) log("TgWsProxy так и не поднялся — проверьте путь в настройках.", "warn");
            log("Автонастройка завершилась с ошибками.", "error");
            done(false, "partial");
        }
    }

    private static async Task WaitForProcessAsync(string[] keywords, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (IsProcessRunning(keywords)) return;
            await Task.Delay(250);
        }
    }

    private static async Task<string> GetLatestGitHubRelease(string repo)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NetFix/1.0");
            var json = await http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest");
            var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        }
        catch { return ""; }
    }

    private static bool IsProcessRunning(string[] keywords)
    {
        try
        {
            return Process.GetProcesses()
                .Any(p =>
                {
                    try
                    {
                        var n = p.ProcessName.ToLowerInvariant();
                        return keywords.Any(k => n.Contains(k.ToLowerInvariant()));
                    }
                    catch { return false; }
                });
        }
        catch { return false; }
    }

    private static void TryLaunch(string path, Action<string, string> log)
    {
        try
        {
            var psi = new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process.Start(psi);
            log($"Запускаю {Path.GetFileName(path)}…", "info");
        }
        catch (Exception e)
        {
            log($"Не удалось запустить {Path.GetFileName(path)}: {e.Message}", "warn");
        }
    }
}

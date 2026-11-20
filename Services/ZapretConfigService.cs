using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;
using NetFix.Models;

namespace NetFix.Services;

public class ZapretConfigService
{
    private static readonly string CacheFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NetFix", "zapret_configs.json");

    private static readonly Regex ConfigRegex = new Regex(@"\[(\d+)/(\d+)\]\s+(.+\.bat)", RegexOptions.Compiled);
    private static readonly Regex TestLineRegex = new Regex(
        @"^\s*(\w+)\s+HTTP:(\w+)\s+TLS1\.2:(\w+)\s+TLS1\.3:(\w+)\s+\|\s+Ping:\s*(\d+)\s*ms",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PingOnlyRegex = new Regex(
        @"^\s*(\w+)\s+Ping:\s*(\d+)\s*ms",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static ZapretConfigCache? LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;
            var json = File.ReadAllText(CacheFile);
            return JsonSerializer.Deserialize<ZapretConfigCache>(json);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveCache(ZapretConfigCache cache)
    {
        try
        {
            var dir = Path.GetDirectoryName(CacheFile);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CacheFile, json);
        }
        catch { }
    }

    public static async Task<(List<ZapretConfig> configs, Process? process)> TestAllConfigsAsync(
        string zapretPath,
        Action<string>? onProgress = null,
        Action<int, int>? onConfigTested = null)
    {
        var configs = new List<ZapretConfig>();
        Process? process = null;

        var zapretDir = Path.GetDirectoryName(zapretPath);
        if (string.IsNullOrEmpty(zapretDir) || !Directory.Exists(zapretDir))
        {
            onProgress?.Invoke("❌ Ошибка: директория Zapret не найдена");
            return (configs, null);
        }

        var testScript = Path.Combine(zapretDir, "utils", "test zapret.ps1");
        if (!File.Exists(testScript))
        {
            onProgress?.Invoke("❌ Ошибка: скрипт test zapret.ps1 не найден");
            return (configs, null);
        }

        onProgress?.Invoke("🚀 Начинаем полное тестирование конфигов...");

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{testScript}\"",
            WorkingDirectory = zapretDir,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };

        process = new Process { StartInfo = psi };

        ZapretConfig? currentConfig = null;
        int totalConfigs = 0;
        int testedConfigs = 0;

        process.OutputDataReceived += (sender, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            var line = e.Data;

            var configMatch = ConfigRegex.Match(line);
            if (configMatch.Success)
            {
                if (currentConfig != null)
                {
                    var successCount = currentConfig.SuccessCount;
                    var totalCount = currentConfig.Tests.Count;
                    var failedTests = totalCount - successCount;

                    currentConfig.IsValid = currentConfig.ErrorCount == 0 && currentConfig.SuccessCount == totalCount && totalCount > 0;

                    if (currentConfig.IsValid)
                    {
                        configs.Add(currentConfig);
                        onProgress?.Invoke($"[HEADER]✅ {currentConfig.Name} - ИДЕАЛЬНЫЙ[/HEADER]");
                        onProgress?.Invoke($"   🔹 Протестировано: {successCount}/{totalCount}, Пинг: {currentConfig.AveragePing}мс");
                        onProgress?.Invoke("");
                    }
                    else if (currentConfig.IsPartiallyUsable)
                    {
                        configs.Add(currentConfig);
                        onProgress?.Invoke($"[HEADER]⚠️ {currentConfig.Name} - ЧАСТИЧНО РАБОЧИЙ[/HEADER]");
                        onProgress?.Invoke($"   🔹 Протестировано: {successCount}/{totalCount}, Пинг: {currentConfig.AveragePing}мс");
                        onProgress?.Invoke("");
                    }
                    else
                    {
                        onProgress?.Invoke($"[HEADER]❌ {currentConfig.Name} - НЕРАБОЧИЙ[/HEADER]");
                        onProgress?.Invoke($"   🔹 Протестировано: {successCount}/{totalCount}, Не работает: {failedTests} сайтов");
                        onProgress?.Invoke("");
                    }

                    testedConfigs++;
                    onConfigTested?.Invoke(testedConfigs, totalConfigs);
                }

                var current = int.Parse(configMatch.Groups[1].Value);
                totalConfigs = int.Parse(configMatch.Groups[2].Value);
                var configName = configMatch.Groups[3].Value;

                currentConfig = new ZapretConfig
                {
                    Name = configName,
                    Tests = new Dictionary<string, ServiceTestResult>()
                };

                onProgress?.Invoke("");
                onProgress?.Invoke($"[HEADER]🔄 Тестирую конфиг [{current}/{totalConfigs}]: {configName}[/HEADER]");
                return;
            }

            var testMatch = TestLineRegex.Match(line);
            if (testMatch.Success && currentConfig != null)
            {
                var serviceName = testMatch.Groups[1].Value;
                var httpStatus = testMatch.Groups[2].Value;
                var tls12Status = testMatch.Groups[3].Value;
                var tls13Status = testMatch.Groups[4].Value;
                var pingStr = testMatch.Groups[5].Value;
                var ping = string.IsNullOrEmpty(pingStr) ? 0 : int.Parse(pingStr);

                var testResult = new ServiceTestResult
                {
                    ServiceName = serviceName,
                    HttpStatus = httpStatus,
                    Tls12Status = tls12Status,
                    Tls13Status = tls13Status,
                    Ping = ping
                };

                currentConfig.Tests[serviceName] = testResult;

                if (serviceName.StartsWith("Discord") || serviceName.StartsWith("YouTube") || serviceName.StartsWith("Google"))
                {
                    var statusText = httpStatus == "OK" && (tls12Status == "OK" || tls13Status == "OK")
                        ? "РАБОТАЕТ"
                        : (httpStatus == "ERROR" || tls12Status == "ERROR" || tls13Status == "ERROR"
                            ? "НЕ РАБОТАЕТ"
                            : "ЧАСТИЧНО");

                    onProgress?.Invoke($"   🟢 {serviceName}: {statusText} | {ping}мс");
                }

                if (testResult.IsSuccess)
                    currentConfig.SuccessCount++;

                if (httpStatus == "ERROR" || tls12Status == "ERROR" || tls13Status == "ERROR")
                    currentConfig.ErrorCount++;

                if (currentConfig.Tests.Count > 0)
                    currentConfig.AveragePing = (int)currentConfig.Tests.Values.Average(t => t.Ping);

                return;
            }

            var pingOnlyMatch = PingOnlyRegex.Match(line);
            if (pingOnlyMatch.Success && currentConfig != null)
            {
                var serviceName = pingOnlyMatch.Groups[1].Value;
                var pingStr = pingOnlyMatch.Groups[2].Value;
                var ping = string.IsNullOrEmpty(pingStr) ? 0 : int.Parse(pingStr);

                var testResult = new ServiceTestResult
                {
                    ServiceName = serviceName,
                    HttpStatus = "OK",
                    Tls12Status = "N/A",
                    Tls13Status = "N/A",
                    Ping = ping
                };

                currentConfig.Tests[serviceName] = testResult;

                if (ping > 0)
                    currentConfig.SuccessCount++;

                if (currentConfig.Tests.Count > 0)
                    currentConfig.AveragePing = (int)currentConfig.Tests.Values.Average(t => t.Ping);
            }
        };

        process.Start();
        process.BeginOutputReadLine();

        try
        {
            await Task.Delay(1000);
            await process.StandardInput.WriteLineAsync("1");
            await Task.Delay(500);
            await process.StandardInput.WriteLineAsync("1");
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        await process.WaitForExitAsync();

        if (currentConfig != null)
        {
            var totalCountEnd = currentConfig.Tests.Count;
            currentConfig.IsValid = currentConfig.ErrorCount == 0 && currentConfig.SuccessCount == totalCountEnd && totalCountEnd > 0;
            if (currentConfig.IsValid || currentConfig.IsPartiallyUsable)
                configs.Add(currentConfig);
        }

        foreach (var cfg in configs)
        {
        }

        configs = configs
            .OrderByDescending(c => c.IsValid)
            .ThenBy(c => c.AveragePing)
            .ToList();

        var idealCount = configs.Count(c => c.IsValid);
        var partialCount = configs.Count(c => c.IsPartiallyUsable);
        onProgress?.Invoke($"Тестирование завершено. Найдено {idealCount} идеальных и {partialCount} частично рабочих конфигов");

        return (configs, process);
    }

    public static async Task<(bool isWorking, string message)> TestSingleConfigAsync(
        string zapretPath,
        string configName,
        Action<string>? onProgress = null)
    {
        var zapretDir = Path.GetDirectoryName(zapretPath);
        if (string.IsNullOrEmpty(zapretDir) || !Directory.Exists(zapretDir))
        {
            onProgress?.Invoke("❌ Ошибка: директория Zapret не найдена");
            return (false, "Ошибка: директория Zapret не найдена");
        }

        var testScript = Path.Combine(zapretDir, "utils", "test zapret.ps1");
        if (!File.Exists(testScript))
        {
            onProgress?.Invoke("❌ Ошибка: скрипт test zapret.ps1 не найден");
            return (false, "Ошибка: скрипт test zapret.ps1 не найден");
        }

        onProgress?.Invoke("🚀 Начинаем тестирование конфига...");

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{testScript}\"",
            WorkingDirectory = zapretDir,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };

        var process = new Process { StartInfo = psi };

        ZapretConfig? currentConfig = null;
        bool foundTargetConfig = false;
        bool configTestComplete = false;

        process.OutputDataReceived += (sender, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            var line = e.Data;

            var configMatch = ConfigRegex.Match(line);
            if (configMatch.Success)
            {
                var configNameFromTest = configMatch.Groups[3].Value;

                if (configNameFromTest == configName)
                {
                    foundTargetConfig = true;
                    currentConfig = new ZapretConfig
                    {
                        Name = configNameFromTest,
                        Tests = new Dictionary<string, ServiceTestResult>()
                    };

                    onProgress?.Invoke("");
                    onProgress?.Invoke($"[HEADER]🔄 Тестирую конфиг: {configName}[/HEADER]");
                }
                else if (foundTargetConfig)
                {
                    configTestComplete = true;
                }

                return;
            }

            if (foundTargetConfig && !configTestComplete && currentConfig != null)
            {
                var testMatch = TestLineRegex.Match(line);
                if (testMatch.Success)
                {
                    var serviceName = testMatch.Groups[1].Value;
                    var httpStatus = testMatch.Groups[2].Value;
                    var tls12Status = testMatch.Groups[3].Value;
                    var tls13Status = testMatch.Groups[4].Value;
                    var pingStr = testMatch.Groups[5].Value;
                    var ping = string.IsNullOrEmpty(pingStr) ? 0 : int.Parse(pingStr);

                    var testResult = new ServiceTestResult
                    {
                        ServiceName = serviceName,
                        HttpStatus = httpStatus,
                        Tls12Status = tls12Status,
                        Tls13Status = tls13Status,
                        Ping = ping
                    };

                    currentConfig.Tests[serviceName] = testResult;

                    var statusText = httpStatus == "OK" && (tls12Status == "OK" || tls13Status == "OK")
                        ? "РАБОТАЕТ"
                        : (httpStatus == "ERROR" || tls12Status == "ERROR" || tls13Status == "ERROR"
                            ? "НЕ РАБОТАЕТ"
                            : "ЧАСТИЧНО");

                    onProgress?.Invoke($"   🟢 {serviceName}: {statusText} | {ping}мс");

                    if (testResult.IsSuccess)
                        currentConfig.SuccessCount++;

                    if (httpStatus == "ERROR" || tls12Status == "ERROR" || tls13Status == "ERROR")
                        currentConfig.ErrorCount++;

                    if (currentConfig.Tests.Count > 0)
                        currentConfig.AveragePing = (int)currentConfig.Tests.Values.Average(t => t.Ping);
                    return;
                }

                var pingOnlyMatch = PingOnlyRegex.Match(line);
                if (pingOnlyMatch.Success)
                {
                    var serviceName = pingOnlyMatch.Groups[1].Value;
                    var pingStr = pingOnlyMatch.Groups[2].Value;
                    var ping = string.IsNullOrEmpty(pingStr) ? 0 : int.Parse(pingStr);

                    var testResult = new ServiceTestResult
                    {
                        ServiceName = serviceName,
                        HttpStatus = "OK",
                        Tls12Status = "N/A",
                        Tls13Status = "N/A",
                        Ping = ping
                    };

                    currentConfig.Tests[serviceName] = testResult;

                    if (ping > 0)
                        currentConfig.SuccessCount++;

                    if (currentConfig.Tests.Count > 0)
                        currentConfig.AveragePing = (int)currentConfig.Tests.Values.Average(t => t.Ping);
                }
            }
        };

        process.Start();
        process.BeginOutputReadLine();

        try
        {
            try
            {
                await Task.Delay(2000);
                await process.StandardInput.WriteLineAsync("1");
                await Task.Delay(1000);
                await process.StandardInput.WriteLineAsync("2");
                await Task.Delay(1000);

                var configFiles = Directory.GetFiles(zapretDir, "*.bat")
                    .Where(f => !Path.GetFileName(f).StartsWith("service", StringComparison.OrdinalIgnoreCase))
                    .Select(Path.GetFileName)
                    .OrderBy(f => f, new NaturalStringComparer())
                    .ToList();

                int configIndex = configFiles.IndexOf(configName) + 1;

                for (int i = 0; i < configFiles.Count; i++)
                {
                }

                if (configIndex > 0)
                {
                    await process.StandardInput.WriteLineAsync(configIndex.ToString());
                    await Task.Delay(1000);
                }
                else
                {
                    onProgress?.Invoke($"❌ Ошибка: не удалось найти конфиг {configName} в списке");
                    try { process.StandardInput.Close(); } catch { }
                    return (false, $"Не удалось найти конфиг {configName} в списке");
                }

                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }

            await process.WaitForExitAsync();

            if (currentConfig != null && foundTargetConfig)
            {
                currentConfig.IsValid = currentConfig.ErrorCount == 0 && currentConfig.SuccessCount == currentConfig.Tests.Count && currentConfig.Tests.Count > 0;

                var successCount = currentConfig.SuccessCount;
                var totalCount = currentConfig.Tests.Count;
                var failedTests = totalCount - successCount;

                if (currentConfig.IsValid)
                {
                    onProgress?.Invoke($"[HEADER]✅ {currentConfig.Name} - РАБОЧИЙ[/HEADER]");
                    onProgress?.Invoke($"   🔹 Протестировано: {successCount}/{totalCount}, Пинг: {currentConfig.AveragePing}мс");
                    return (true, $"Конфиг работает! Пройдено {successCount}/{totalCount} тестов");
                }
                else
                {
                    onProgress?.Invoke($"[HEADER]❌ {currentConfig.Name} - НЕРАБОЧИЙ[/HEADER]");
                    onProgress?.Invoke($"   🔹 Протестировано: {successCount}/{totalCount}, Не работает: {failedTests} сайтов");
                    return (false, $"Конфиг не проходит все тесты. Пройдено {successCount}/{totalCount}");
                }
            }
            else
            {
                onProgress?.Invoke($"❌ Ошибка: конфиг {configName} не был протестирован");
                onProgress?.Invoke($"⚠️ ВОЗМОЖНО У ВАС ЗАПУЩЕН VPN! Закройте VPN и попробуйте ещё раз!");
                return (false, $"Конфиг {configName} не был протестирован. Возможно, у вас запущен VPN - закройте его и попробуйте снова.");
            }
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            process.Dispose();
        }
    }

    private static async Task<bool> TestDiscordConnection()
    {
        try
        {
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var response = await client.GetAsync("https://discord.com");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string GetStatusEmojis(string http, string tls12, string tls13)
    {
        var httpEmoji = http switch
        {
            "OK" => "✅",
            "ERROR" => "❌",
            "UNSUP" => "⚠️",
            _ => "❓"
        };

        var tls12Emoji = tls12 switch
        {
            "OK" => "✅",
            "ERROR" => "❌",
            "UNSUP" => "⚠️",
            _ => "❓"
        };

        var tls13Emoji = tls13 switch
        {
            "OK" => "✅",
            "ERROR" => "❌",
            "UNSUP" => "⚠️",
            _ => "❓"
        };

        return $"{httpEmoji} {http} | {tls12Emoji} {tls12} | {tls13Emoji} {tls13}";
    }

    public static Task<bool> ApplyConfigAsync(string zapretPath, string configName)
        => Task.Run(() => ApplyConfigCoreAsync(zapretPath, configName));

    private static async Task<bool> ApplyConfigCoreAsync(string zapretPath, string configName)
    {
        try
        {
            var zapretDir = Path.GetDirectoryName(zapretPath);
            if (string.IsNullOrEmpty(zapretDir))
                return false;

            var configPath = Path.Combine(zapretDir, configName);
            if (!File.Exists(configPath))
                return false;

            var binPath = Path.Combine(zapretDir, "bin");
            var winwsExe = Path.Combine(binPath, "winws.exe");
            if (!File.Exists(winwsExe))
                return false;

            var argTokens = await ParseConfigArgTokensAsync(configPath, zapretDir, binPath).ConfigureAwait(false);
            if (argTokens.Count == 0)
                return false;

            await StopAndRemoveServiceAsync("zapret").ConfigureAwait(false);
            await EnableTcpTimestampsAsync().ConfigureAwait(false);

            // сначала служба (как в service.bat), потом прямой winws, потом сам .bat
            if (await InstallAndStartServiceAsync(winwsExe, argTokens, configName).ConfigureAwait(false))
                return true;

            if (await TryStartWinwsDirectAsync(winwsExe, argTokens, binPath).ConfigureAwait(false))
                return true;

            if (await TryStartViaStrategyBatAsync(configPath, zapretDir).ConfigureAwait(false))
                return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    public static Task<bool> IsZapretUpAsync()
    {
        try
        {
            if (Process.GetProcessesByName("winws").Length > 0)
                return Task.FromResult(true);
            if (Process.GetProcessesByName("nfqws").Length > 0)
                return Task.FromResult(true);
        }
        catch { }

        return Task.FromResult(false);
    }

    // без .exe — иначе GetProcessesByName пустой
    public static void KillWinwsProcesses()
    {
        foreach (var name in new[] { "winws", "nfqws" })
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try { proc.Kill(true); proc.Dispose(); } catch { }
            }
        }
    }

    public static void KillTgWsProcesses()
    {
        foreach (var name in new[] { "TgWsProxy", "tg-ws-proxy", "tgwsproxy" })
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try { proc.Kill(true); proc.Dispose(); } catch { }
            }
        }
    }

    private static async Task<bool> WaitForWinwsAsync(int attempts = 12, int delayMs = 300)
    {
        for (int i = 0; i < attempts; i++)
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            if (await IsZapretUpAsync().ConfigureAwait(false))
                return true;
        }
        return false;
    }

    private static async Task<bool> TryStartWinwsDirectAsync(string winwsExe, List<string> argTokens, string workingDir)
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("winws"))
            {
                try { proc.Kill(); proc.Dispose(); } catch { }
            }

            var psi = new ProcessStartInfo
            {
                FileName = winwsExe,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in argTokens)
                psi.ArgumentList.Add(a);

            Process.Start(psi);
            return await WaitForWinwsAsync().ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    // запасной вариант — просто прогнать .bat, как вручную
    private static async Task<bool> TryStartViaStrategyBatAsync(string configPath, string zapretDir)
    {
        try
        {
            KillWinwsProcesses();

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{configPath}\"\"",
                WorkingDirectory = zapretDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
            return await WaitForWinwsAsync(15, 350).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    public static async Task<(bool Ok, string? UsedConfig)> TryStartBestConfigAsync(
        string zapretPath,
        ZapretConfigCache? cache = null,
        Action<string>? log = null,
        int maxAlts = 12)
    {
        cache ??= LoadCache() ?? new ZapretConfigCache();
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var zapretDir = Path.GetDirectoryName(zapretPath) ?? "";

        async Task<bool> TryOne(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || !tried.Add(name))
                return false;

            var batPath = Path.Combine(zapretDir, name);
            if (!File.Exists(batPath))
                return false;

            log?.Invoke(name);
            if (!await ApplyConfigAsync(zapretPath, name).ConfigureAwait(false))
                return false;

            cache.CurrentConfig = name;
            SaveCache(cache);
            return true;
        }

        if (await TryOne(cache.CurrentConfig).ConfigureAwait(false))
            return (true, cache.CurrentConfig);

        // в кэше идеальные уже сверху по пингу
        var candidates = cache.GetSelectableConfigs()
            .Select(c => c.Name)
            .Where(n => !string.Equals(n, cache.CurrentConfig, StringComparison.OrdinalIgnoreCase))
            .Take(maxAlts)
            .ToList();

        foreach (var name in candidates)
        {
            if (await TryOne(name).ConfigureAwait(false))
                return (true, name);
        }

        return (false, null);
    }

    // вытаскиваем аргументы winws из strategy.bat
    private static async Task<List<string>> ParseConfigArgTokensAsync(string configPath, string zapretDir, string binPath)
    {
        try
        {
            var lines = await File.ReadAllLinesAsync(configPath).ConfigureAwait(false);
            var listsPath = Path.Combine(zapretDir, "lists");
            var fullText = new StringBuilder();
            bool capture = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("::") || trimmed.StartsWith("rem ", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (trimmed.Contains("winws.exe", StringComparison.OrdinalIgnoreCase))
                {
                    capture = true;
                    var idx = trimmed.IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                        trimmed = trimmed[(idx + "winws.exe".Length)..].Trim();
                    // хвост от "%BIN%winws.exe"
                    if (trimmed.StartsWith('"'))
                        trimmed = trimmed[1..].Trim();
                }

                if (!capture) continue;

                if (trimmed.EndsWith('^'))
                    trimmed = trimmed[..^1].TrimEnd();

                if (trimmed.Length == 0) continue;
                fullText.Append(' ').Append(trimmed);
            }

            var text = fullText.ToString();
            text = text.Replace("%BIN%", binPath + "\\", StringComparison.OrdinalIgnoreCase);
            text = text.Replace("%LISTS%", listsPath + "\\", StringComparison.OrdinalIgnoreCase);
            var (gameTcp, gameUdp) = ZapretOptionsService.GetGameFilterPorts(zapretDir);
            text = text.Replace("%GameFilter%", gameTcp, StringComparison.OrdinalIgnoreCase);
            text = text.Replace("%GameFilterTCP%", gameTcp, StringComparison.OrdinalIgnoreCase);
            text = text.Replace("%GameFilterUDP%", gameUdp, StringComparison.OrdinalIgnoreCase);

            return SplitArgs(text)
                .Select(t => t.Trim().Trim('"'))
                .Where(t => t.Length > 0 && t != "^")
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static List<string> SplitArgs(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        foreach (char c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }

    private static async Task StopAndRemoveServiceAsync(string serviceName)
    {
        try
        {
            KillWinwsProcesses();

            await ProcessRunner.RunAsync("sc", $"stop {serviceName}", 8000).ConfigureAwait(false);
            await Task.Delay(300).ConfigureAwait(false);
            await ProcessRunner.RunAsync("sc", $"delete {serviceName}", 8000).ConfigureAwait(false);
            await Task.Delay(400).ConfigureAwait(false);
        }
        catch { }
    }

    private static async Task EnableTcpTimestampsAsync()
    {
        try
        {
            await ProcessRunner.RunAsync("netsh", "interface tcp set global timestamps=enabled", 5000)
                .ConfigureAwait(false);
        }
        catch { }
    }

    // sc create + ImagePath в реестре (кавычки в binPath= у sc легко ломаются)
    private static async Task<bool> InstallAndStartServiceAsync(string exePath, List<string> argTokens, string configName)
    {
        try
        {
            await ProcessRunner.RunAsync(
                "sc",
                $"create zapret binPath= \"{exePath}\" DisplayName= \"zapret\" start= auto",
                8000).ConfigureAwait(false);

            var imagePath = BuildImagePath(exePath, argTokens);
            try
            {
                Registry.SetValue(
                    @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\zapret",
                    "ImagePath",
                    imagePath,
                    RegistryValueKind.ExpandString);
            }
            catch
            {
                return false;
            }

            await ProcessRunner.RunAsync("sc", "description zapret \"Zapret DPI bypass software\"", 5000)
                .ConfigureAwait(false);

            var strategyName = Path.GetFileNameWithoutExtension(configName);
            await ProcessRunner.RunAsync(
                "reg",
                $"add \"HKLM\\System\\CurrentControlSet\\Services\\zapret\" /v zapret-discord-youtube /t REG_SZ /d \"{strategyName}\" /f",
                5000).ConfigureAwait(false);

            await StartServiceAsync("zapret").ConfigureAwait(false);
            return await WaitForWinwsAsync().ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildImagePath(string exePath, List<string> argTokens)
    {
        var sb = new StringBuilder();
        sb.Append('"').Append(exePath).Append('"');
        foreach (var a in argTokens)
        {
            sb.Append(' ');
            if (a.Contains(' ') || a.Contains('\t'))
                sb.Append('"').Append(a).Append('"');
            else
                sb.Append(a);
        }
        return sb.ToString();
    }

    private static async Task StartServiceAsync(string serviceName)
    {
        try
        {
            var (code, _) = await ProcessRunner.RunAsync("sc", $"start {serviceName}", 15000)
                .ConfigureAwait(false);
            if (code != 0 && code != 1056)
                await ProcessRunner.RunAsync("net", $"start {serviceName}", 15000).ConfigureAwait(false);
        }
        catch { }
    }

    public static string GetModBatName(string modFolderName)
        => $"mod_{modFolderName}.bat";

    public static void InstallModBat(string zapretServicePath, string modFolderPath, string modFolderName)
    {
        var zapretDir = Path.GetDirectoryName(zapretServicePath) ?? @"C:\Zapret";
        var src = Path.Combine(modFolderPath, "strategy.bat");
        var dst = Path.Combine(zapretDir, GetModBatName(modFolderName));
        if (File.Exists(src))
            File.Copy(src, dst, overwrite: true);
    }

    public static void UninstallModBat(string zapretServicePath, string modFolderName)
    {
        var zapretDir = Path.GetDirectoryName(zapretServicePath) ?? @"C:\Zapret";
        var dst = Path.Combine(zapretDir, GetModBatName(modFolderName));
        if (File.Exists(dst))
            File.Delete(dst);
    }

    public static void InjectModConfig(string zapretServicePath, string modName, string modFolderName)
    {
        var cache = LoadCache() ?? new ZapretConfigCache
        {
            ValidConfigs = [],
            PartialConfigs = [],
        };

        cache.ValidConfigs ??= [];
        cache.PartialConfigs ??= [];

        var batName = GetModBatName(modFolderName);
        cache.ValidConfigs.RemoveAll(c => c.Name == batName);
        cache.PartialConfigs.RemoveAll(c => c.Name == batName);

        cache.ValidConfigs.Insert(0, new ZapretConfig
        {
            Name = batName,
            IsValid = true,
            IsFromMod = true,
            ModName = modName
        });

        SaveCache(cache);
    }

    public static void RemoveAllModConfigs()
    {
        var cache = LoadCache();
        if (cache is null) return;

        cache.ValidConfigs?.RemoveAll(c => c.IsFromMod);
        cache.PartialConfigs?.RemoveAll(c => c.IsFromMod);

        SaveCache(cache);
    }

    public static void RemoveModConfig(string zapretServicePath, string modFolderName)
    {
        var cache = LoadCache();
        if (cache is null) return;

        var batName = GetModBatName(modFolderName);
        cache.ValidConfigs?.RemoveAll(c => c.Name == batName);
        cache.PartialConfigs?.RemoveAll(c => c.Name == batName);

        if (cache.CurrentConfig == batName)
            cache.CurrentConfig = null;

        SaveCache(cache);
    }
}

public class NaturalStringComparer : IComparer<string>
{
    public int Compare(string? x, string? y)
    {
        if (x == null && y == null) return 0;
        if (x == null) return -1;
        if (y == null) return 1;

        int ix = 0, iy = 0;
        while (ix < x.Length && iy < y.Length)
        {
            if (char.IsDigit(x[ix]) && char.IsDigit(y[iy]))
            {
                var numX = GetNumber(x, ref ix);
                var numY = GetNumber(y, ref iy);

                var result = numX.CompareTo(numY);
                if (result != 0) return result;
            }
            else
            {
                var result = string.Compare(x, ix, y, iy, 1, StringComparison.OrdinalIgnoreCase);
                if (result != 0) return result;
                ix++;
                iy++;
            }
        }

        return x.Length.CompareTo(y.Length);
    }

    private static int GetNumber(string s, ref int index)
    {
        int start = index;
        while (index < s.Length && char.IsDigit(s[index]))
            index++;

        return int.Parse(s.Substring(start, index - start));
    }
}

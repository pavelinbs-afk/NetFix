using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NetFix.Models;

namespace NetFix.Services;

public static class AntiCheatBypassService
{
    private static readonly string[] AntiCheatProcesses =
    [
        "easyanticheat",
        "easyanticheat_eos",
        "easyanticheat_eos_setup",
        "rustclient",
        "rust",
        "r5apex",
        "fortniteclient-win64-shipping",
        "deadbydaylight-win64-shipping",
        "squadgame",
        "unturned",
        "beservice",
        "battleye"
    ];

    private static CancellationTokenSource? _watcherCts;
    private static bool _isBypassingNow;

    public static async Task StopZapretAndWinDivertAsync()
    {
        await RunCommandAsync("sc", "stop zapret");
        await Task.Delay(200);

        ZapretConfigService.KillWinwsProcesses();

        await RunCommandAsync("sc", "stop windivert");
        await RunCommandAsync("sc", "stop windivert14");
        await Task.Delay(500);
    }

    public static async Task<bool> StartZapretAsync(string? zapretPath = null)
    {
        var settings = SettingsService.Load();
        var path = !string.IsNullOrEmpty(zapretPath) ? zapretPath : settings.ZapretPath;
        var cache = ZapretConfigService.LoadCache();

        if (!string.IsNullOrEmpty(path) && File.Exists(path)
            && cache is { CurrentConfig.Length: > 0 })
        {
            return await ZapretConfigService.ApplyConfigAsync(path, cache.CurrentConfig)
                .ConfigureAwait(false);
        }

        await RunCommandAsync("sc", "start zapret");
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(300).ConfigureAwait(false);
            if (await ZapretConfigService.IsZapretUpAsync().ConfigureAwait(false))
                return true;
        }

        return false;
    }

    public static async Task RunCommandAsync(string fileName, string args, int timeoutMs = 10000)
    {
        await ProcessRunner.RunAsync(fileName, args, timeoutMs).ConfigureAwait(false);
    }

    public static void StartWatcher(Action<string>? onBypassTriggered = null)
    {
        StopWatcher();
        _watcherCts = new CancellationTokenSource();
        var token = _watcherCts.Token;

        Task.Run(async () =>
        {
            var knownPids = new HashSet<int>();

            foreach (var name in AntiCheatProcesses)
            {
                foreach (var p in Process.GetProcessesByName(name))
                    knownPids.Add(p.Id);
            }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1500, token);

                    if (_isBypassingNow) continue;

                    var status = DiagnosticsEngine.CheckAppStatus();
                    if (!status.ZapretRunning) continue;

                    bool newGameDetected = false;
                    string detectedName = "";

                    foreach (var name in AntiCheatProcesses)
                    {
                        var procs = Process.GetProcessesByName(name);
                        foreach (var p in procs)
                        {
                            if (!knownPids.Contains(p.Id))
                            {
                                knownPids.Add(p.Id);
                                newGameDetected = true;
                                detectedName = p.ProcessName;
                                break;
                            }
                        }
                        if (newGameDetected) break;
                    }

                    knownPids.RemoveWhere(pid =>
                    {
                        try { using var p = Process.GetProcessById(pid); return p.HasExited; }
                        catch { return true; }
                    });

                    if (newGameDetected)
                    {
                        _isBypassingNow = true;
                        onBypassTriggered?.Invoke(detectedName);

                        await StopZapretAndWinDivertAsync();
                        await Task.Delay(12000, token);
                        await StartZapretAsync();

                        _isBypassingNow = false;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(2000, token);
                }
            }
        }, token);
    }

    public static void StopWatcher()
    {
        _watcherCts?.Cancel();
        _watcherCts = null;
    }
}

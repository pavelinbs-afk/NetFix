using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace NetFix.Services;

// Process.Start с таймаутом, чтобы не зависать на stdout/stderr
public static class ProcessRunner
{
    public static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName, string args, int timeoutMs = 12000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return (-1, "");

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                try { await stdoutTask.ConfigureAwait(false); } catch { }
                try { await stderrTask.ConfigureAwait(false); } catch { }
                return (-1, "timeout");
            }

            var output = await stdoutTask.ConfigureAwait(false);
            var error = await stderrTask.ConfigureAwait(false);
            return (proc.ExitCode, string.IsNullOrWhiteSpace(output) ? error : output);
        }
        catch
        {
            return (-1, "");
        }
    }

    // Для коротких команд вроде schtasks — без редиректа, просто ждём с таймаутом.
    public static int Run(string fileName, string args, int timeoutMs = 12000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return -1;

            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(true); } catch { }
                return -1;
            }

            return proc.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}

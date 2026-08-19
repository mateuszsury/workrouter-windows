using System.Diagnostics;
using System.Text;

namespace WorkRouter.Infrastructure;

internal sealed record ProcessExecutionResult(int ExitCode, string StandardOutput, string StandardError);

internal static class PowerShellRunner
{
    public static async Task<ProcessExecutionResult> RunAsync(
        string script,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value ?? string.Empty;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Nie udało się uruchomić Windows PowerShell.");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException)
            {
                // Process has already exited.
            }

            throw;
        }

        return new ProcessExecutionResult(
            process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false));
    }
}

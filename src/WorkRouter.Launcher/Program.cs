using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WorkRouter.Launcher;

internal static class Program
{
    private const string MutexName = @"Local\WorkRouter.Launcher";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            if (!PanelWindow.TryBringToFront())
            {
                PanelWindow.OpenFromState();
            }
            return;
        }

        Application.Run(new WorkRouterApplicationContext());
        GC.KeepAlive(mutex);
    }
}

internal sealed class WorkRouterApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private EndpointInfo? _endpoint;

    public WorkRouterApplicationContext()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "WorkRouter — łączenie z usługą…",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _notifyIcon.DoubleClick += (_, _) => OpenPanel();
        _ = InitializeAsync();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Otwórz panel", null, (_, _) => OpenPanel());
        menu.Items.Add("Uruchom router", null, async (_, _) => await PostAsync("/api/router/start"));
        menu.Items.Add("Zatrzymaj router", null, async (_, _) => await PostAsync("/api/router/stop"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Zakończ WorkRouter", null, async (_, _) => await ExitAsync());
        return menu;
    }

    private async Task InitializeAsync()
    {
        try
        {
            _endpoint = await WaitForEndpointAsync(TimeSpan.FromSeconds(12)).ConfigureAwait(true);
            _httpClient.DefaultRequestHeaders.Add("X-WorkRouter-Token", _endpoint.Token);
            _notifyIcon.Text = "WorkRouter — gotowy";
            OpenPanel();
        }
        catch (Exception exception)
        {
            _notifyIcon.Text = "WorkRouter — usługa niedostępna";
            MessageBox.Show(
                "Nie można połączyć się z usługą WorkRouter. Uruchom instalator lub usługę systemową.\n\n" + exception.Message,
                "WorkRouter",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private async void OpenPanel()
    {
        if (_endpoint is null)
        {
            return;
        }

        if (PanelWindow.TryBringToFront())
        {
            return;
        }

        try
        {
            using var response = await _httpClient.PostAsync(
                _endpoint.Url.TrimEnd('/') + "/api/bootstrap-ticket",
                null).ConfigureAwait(true);
            response.EnsureSuccessStatusCode();
            var ticket = await response.Content.ReadFromJsonAsync<BootstrapTicketResponse>().ConfigureAwait(true)
                ?? throw new InvalidOperationException("Usługa nie zwróciła biletu uruchomieniowego.");
            var url = $"{_endpoint.Url.TrimEnd('/')}/bootstrap-ticket?ticket={Uri.EscapeDataString(ticket.Ticket)}";
            PanelWindow.Open(url);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "Nie można otworzyć panelu WorkRouter.\n\n" + exception.Message,
                "WorkRouter",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private async Task PostAsync(string path)
    {
        if (_endpoint is null)
        {
            return;
        }

        try
        {
            using var response = await _httpClient.PostAsync(_endpoint.Url.TrimEnd('/') + path, null).ConfigureAwait(true);
            var result = await response.Content.ReadFromJsonAsync<OperationResponse>().ConfigureAwait(true);
            _notifyIcon.ShowBalloonTip(
                4000,
                result?.Success == true ? "WorkRouter" : "WorkRouter — błąd",
                result?.Message ?? response.ReasonPhrase ?? "Brak odpowiedzi",
                result?.Success == true ? ToolTipIcon.Info : ToolTipIcon.Error);
        }
        catch (Exception exception)
        {
            _notifyIcon.ShowBalloonTip(4000, "WorkRouter — błąd", exception.Message, ToolTipIcon.Error);
        }
    }

    private async Task ExitAsync()
    {
        await PostAsync("/api/router/stop").ConfigureAwait(true);
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _httpClient.Dispose();
        ExitThread();
    }

    private static async Task<EndpointInfo> WaitForEndpointAsync(TimeSpan timeout)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WorkRouter",
            "endpoint.json");
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                await using var stream = File.OpenRead(path);
                var endpoint = await JsonSerializer.DeserializeAsync<EndpointInfo>(stream, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }).ConfigureAwait(false);
                if (endpoint is { Url.Length: > 0, Token.Length: > 20 })
                {
                    return endpoint;
                }
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        throw new TimeoutException("Plik endpoint.json nie pojawił się w wymaganym czasie.");
    }

    private sealed record OperationResponse(bool Success, string Code, string Message);
    private sealed record BootstrapTicketResponse(string Ticket, int ExpiresInSeconds);
}

internal static class PanelWindow
{
    private const int SwRestore = 9;
    private const string PanelTitle = "WorkRouter — panel operacyjny";

    public static void Open(string url)
    {
        var browser = FindAppBrowser();
        if (browser is null)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        else
        {
            var profile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WorkRouter",
                "BrowserProfile");
            Directory.CreateDirectory(profile);
            var startInfo = new ProcessStartInfo(browser) { UseShellExecute = false };
            startInfo.ArgumentList.Add($"--app={url}");
            startInfo.ArgumentList.Add($"--user-data-dir={profile}");
            startInfo.ArgumentList.Add("--no-first-run");
            startInfo.ArgumentList.Add("--start-maximized");
            Process.Start(startInfo);
        }
        _ = BringToFrontWhenReadyAsync();
    }

    public static void OpenFromState()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "WorkRouter",
                "endpoint.json");
            var endpoint = JsonSerializer.Deserialize<EndpointInfo>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (endpoint is { Url.Length: > 0, Token.Length: > 20 })
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                client.DefaultRequestHeaders.Add("X-WorkRouter-Token", endpoint.Token);
                using var response = client.PostAsync(endpoint.Url.TrimEnd('/') + "/api/bootstrap-ticket", null)
                    .GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                var ticket = response.Content.ReadFromJsonAsync<BootstrapTicketResponse>()
                    .GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException("Usługa nie zwróciła biletu uruchomieniowego.");
                Open($"{endpoint.Url.TrimEnd('/')}/bootstrap-ticket?ticket={Uri.EscapeDataString(ticket.Ticket)}");
            }
        }
        catch
        {
            // The primary tray instance will surface service errors. A second
            // shortcut activation must remain fast and unobtrusive.
        }
    }

    public static bool TryBringToFront()
    {
        var browserPath = FindAppBrowser();
        var browserProcessName = browserPath is null ? null : Path.GetFileNameWithoutExtension(browserPath);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (browserProcessName is not null &&
                        !string.Equals(process.ProcessName, browserProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (process.MainWindowHandle == IntPtr.Zero ||
                        !process.MainWindowTitle.StartsWith(PanelTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    ShowWindowAsync(process.MainWindowHandle, SwRestore);
                    BringWindowToTop(process.MainWindowHandle);
                    SetForegroundWindow(process.MainWindowHandle);
                    return true;
                }
                catch (InvalidOperationException)
                {
                    // A browser process can disappear while it is enumerated.
                }
            }
        }
        return false;
    }

    private static async Task BringToFrontWhenReadyAsync()
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (TryBringToFront())
            {
                return;
            }
            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    private static string? FindAppBrowser()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}

internal sealed record EndpointInfo(string Url, string Token);
internal sealed record BootstrapTicketResponse(string Ticket, int ExpiresInSeconds);

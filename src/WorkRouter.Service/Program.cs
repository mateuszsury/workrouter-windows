using System.Text.Json.Serialization;
using WorkRouter.Abstractions;
using WorkRouter.Configuration;
using WorkRouter.Core.Networking;
using WorkRouter.Monitoring;
using WorkRouter.Models;
using WorkRouter.Orchestration;
using WorkRouter.Service;
using WorkRouter.Sharing;

var applicationRoot = AppContext.BaseDirectory;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = applicationRoot,
    WebRootPath = Path.Combine(applicationRoot, "wwwroot")
});
builder.Host.UseWindowsService(options => options.ServiceName = "WorkRouter");
builder.WebHost.UseUrls(builder.Configuration["WorkRouter:Url"] ?? "http://127.0.0.1:17437");
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddSingleton<ServiceTokenManager>();
builder.Services.AddSingleton<RouterConfigurationStore>();
builder.Services.AddSingleton<TrafficPreferencesStore>();
builder.Services.AddSingleton<WindowsHotspotController>();
builder.Services.AddSingleton<IHotspotController>(provider => provider.GetRequiredService<WindowsHotspotController>());
builder.Services.AddSingleton<WfpNetworkIsolation>();
builder.Services.AddSingleton<INetworkIsolation>(provider => provider.GetRequiredService<WfpNetworkIsolation>());
builder.Services.AddSingleton<IShareManager, WindowsShareManager>();
builder.Services.AddSingleton<IUsageMonitor, NetworkInterfaceUsageMonitor>();
builder.Services.AddSingleton<RawSocketTrafficMonitor>();
builder.Services.AddSingleton<ITrafficMonitor>(provider => provider.GetRequiredService<RawSocketTrafficMonitor>());
builder.Services.AddSingleton<IStartupShortcutManager, StartupShortcutManager>();
builder.Services.AddSingleton<RouterCoordinator>();
builder.Services.AddSingleton<IRouterCoordinator>(provider => provider.GetRequiredService<RouterCoordinator>());
builder.Services.AddHostedService<RouterWatchdogService>();
builder.Services.AddHostedService<RouterAutoStartService>();

var app = builder.Build();
app.UseMiddleware<ServiceSecurityMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = "no-cache";
    }
});

app.MapGet("/api/status", async (IRouterCoordinator coordinator, CancellationToken cancellationToken) =>
{
    var status = await coordinator.GetStatusAsync(cancellationToken).ConfigureAwait(false);
    return Results.Json(ApiModels.Status(status));
});

app.MapGet("/api/clients", async (IRouterCoordinator coordinator, CancellationToken cancellationToken) =>
{
    var status = await coordinator.GetStatusAsync(cancellationToken).ConfigureAwait(false);
    return Results.Json(ApiModels.Clients(status.Clients));
});

app.MapGet("/api/events", (IRouterCoordinator coordinator, long? afterId) =>
    Results.Json(new { events = coordinator.GetEvents(afterId ?? 0) }));

app.MapGet("/api/preferences", async (TrafficPreferencesStore preferences, IStartupShortcutManager startup, CancellationToken cancellationToken) =>
{
    var value = await preferences.LoadAsync(cancellationToken).ConfigureAwait(false);
    var actual = await startup.IsOpenPanelAtLoginEnabledAsync(cancellationToken).ConfigureAwait(false);
    return Results.Json(value with { OpenPanelAtLogin = actual });
});

app.MapPut("/api/preferences", async (
    PreferencesRequest request,
    TrafficPreferencesStore preferences,
    ITrafficMonitor traffic,
    IRouterCoordinator coordinator,
    IStartupShortcutManager startup,
    CancellationToken cancellationToken) =>
{
    var current = await preferences.LoadAsync(cancellationToken).ConfigureAwait(false);
    current = current with
    {
        OpenPanelAtLogin = await startup.IsOpenPanelAtLoginEnabledAsync(cancellationToken).ConfigureAwait(false)
    };
    var updated = (current with
    {
        AutoStartRouter = request.AutoStartRouter ?? current.AutoStartRouter,
        TrafficInspectionEnabled = request.TrafficInspectionEnabled ?? current.TrafficInspectionEnabled,
        RetentionHours = request.RetentionHours ?? current.RetentionHours,
        OpenPanelAtLogin = request.OpenPanelAtLogin ?? current.OpenPanelAtLogin
    }).Normalize();
    var shortcut = await startup.SetOpenPanelAtLoginAsync(updated.OpenPanelAtLogin, cancellationToken).ConfigureAwait(false);
    if (!shortcut.Success) return Results.Json(new { success = false, preferences = current, shortcut }, statusCode: StatusCodes.Status409Conflict);
    var saved = await preferences.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
    traffic.UpdatePreferences(saved);
    var requiresRouterRestart = false;
    var status = await coordinator.GetStatusAsync(cancellationToken).ConfigureAwait(false);
    if (status.State == RouterOperationalState.On && current.TrafficInspectionEnabled != saved.TrafficInspectionEnabled)
    {
        if (!saved.TrafficInspectionEnabled) await traffic.StopAsync(cancellationToken).ConfigureAwait(false);
        else requiresRouterRestart = true;
    }
    return Results.Json(new { success = true, preferences = saved, shortcut, requiresRouterRestart });
});

app.MapGet("/api/traffic/summary", (ITrafficMonitor traffic, int? windowMinutes) =>
    Results.Json(traffic.GetSummary(windowMinutes ?? 60)));

app.MapGet("/api/traffic/events", (ITrafficMonitor traffic, long? afterId) =>
    Results.Json(new { events = traffic.GetEvents(afterId ?? 0), status = traffic.Status }));

app.MapPost("/api/traffic/clear", (ITrafficMonitor traffic) => { traffic.Clear(); return Results.Ok(new { success = true, code = "ok", message = "Historia telemetrii wyczyszczona." }); });

app.MapPost("/api/bootstrap-ticket", (ServiceTokenManager tokens) =>
    Results.Json(new { ticket = tokens.CreateBootstrapTicket(), expiresInSeconds = 30 }));

app.MapPost("/api/router/start", async (IRouterCoordinator coordinator, CancellationToken cancellationToken) =>
    ToResult(await coordinator.StartAsync(cancellationToken).ConfigureAwait(false)));

app.MapPost("/api/router/stop", async (IRouterCoordinator coordinator, CancellationToken cancellationToken) =>
    ToResult(await coordinator.StopAsync(cancellationToken).ConfigureAwait(false)));

app.MapPut("/api/settings", async (
    SettingsRequest request,
    RouterConfigurationStore configuration,
    IRouterCoordinator coordinator,
    IHotspotController hotspot,
    CancellationToken cancellationToken) =>
{
    var current = await configuration.LoadAsync(cancellationToken).ConfigureAwait(false);
    var updated = current with
    {
        Ssid = request.Ssid,
        Band = ApiModels.FromUiBand(request.Band),
        Passphrase = string.IsNullOrWhiteSpace(request.Password) ? current.Passphrase : request.Password
    };
    var before = await coordinator.GetStatusAsync(cancellationToken).ConfigureAwait(false);
    var wasRunning = before.State == RouterOperationalState.On;

    async Task<(bool Success, string Detail)> RestorePreviousAsync()
    {
        try
        {
            // Always stop first: a failed StartAsync may leave the coordinator Faulted
            // while the hotspot/WFP transaction is only partially unwound.
            var stop = await coordinator.StopAsync(cancellationToken).ConfigureAwait(false);
            if (!stop.Success && !string.Equals(stop.Code, "already_off", StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"stop: {stop.Code}: {stop.Message}");
            }

            // UpdateSettingsAsync provisions the share before committing, so this also
            // restores the previous workshare password before the old router starts.
            var restore = await coordinator.UpdateSettingsAsync(current, cancellationToken).ConfigureAwait(false);
            if (!restore.Success)
            {
                return (false, $"settings: {restore.Code}: {restore.Message}");
            }

            if (!wasRunning)
            {
                return (true, "Przywrócono poprzednie ustawienia; router pozostał wyłączony.");
            }

            var restart = await coordinator.StartAsync(cancellationToken).ConfigureAwait(false);
            return restart.Success
                ? (true, "Przywrócono poprzednią konfigurację, hasło udziału i działanie routera.")
                : (false, $"restart: {restart.Code}: {restart.Message}");
        }
        catch (Exception exception)
        {
            return (false, $"rollback_exception: {exception.Message}");
        }
    }

    if (wasRunning)
    {
        var stop = await coordinator.StopAsync(cancellationToken).ConfigureAwait(false);
        if (!stop.Success) return ToResult(stop);
    }

    var update = await coordinator.UpdateSettingsAsync(updated, cancellationToken).ConfigureAwait(false);
    if (!update.Success)
    {
        var restored = wasRunning
            ? await RestorePreviousAsync().ConfigureAwait(false)
            : (true, "Poprzednie ustawienia pozostały aktywne.");
        // Even while already stopped, EnsureAsync may have touched the local
        // workshare account before reporting an error. Re-run the old settings
        // transaction so the saved configuration and SMB credential agree.
        if (!wasRunning)
        {
            restored = await RestorePreviousAsync().ConfigureAwait(false);
        }
        return Results.Json(new
        {
            success = false,
            code = update.Code,
            message = update.Message,
            rolledBack = restored.Item1,
            routerRestored = restored.Item1,
            rollbackDetail = restored.Item2
        }, statusCode: restored.Item1 ? StatusCodes.Status409Conflict : StatusCodes.Status500InternalServerError);
    }

    if (!wasRunning) return ToResult(update);
    var start = await coordinator.StartAsync(cancellationToken).ConfigureAwait(false);
    if (!start.Success)
    {
        var restored = await RestorePreviousAsync().ConfigureAwait(false);
        return Results.Json(new
        {
            success = false,
            code = "settings_restart_failed",
            message = start.Message,
            rolledBack = restored.Item1,
            routerRestored = restored.Item1,
            rollbackDetail = restored.Item2,
            activeBand = (string?)null,
            bandConfirmed = false
        }, statusCode: restored.Item1 ? StatusCodes.Status409Conflict : StatusCodes.Status500InternalServerError);
    }
    var active = await hotspot.InspectAsync(cancellationToken).ConfigureAwait(false);
    var requestedBand = updated.Band == "TwoPointFourGigahertz" ? "TwoPointFourGigahertz" : updated.Band == "FiveGigahertz" ? "FiveGigahertz" : "Auto";
    // For every requested mode, including Auto, an absent observation is failure.
    // This prevents reporting a successful band switch when WinRT did not expose
    // the active AP configuration.
    var confirmed = active.ActiveBand is not null
        && (requestedBand == "Auto" || string.Equals(active.ActiveBand, requestedBand, StringComparison.OrdinalIgnoreCase));
    if (!confirmed)
    {
        var restored = await RestorePreviousAsync().ConfigureAwait(false);
        return Results.Json(new
        {
            success = false,
            code = requestedBand == "Auto" ? "active_band_unconfirmed" : "band_mismatch",
            message = $"Nie potwierdzono aktywnego pasma ({active.ActiveBand ?? "brak danych"}); przywrócono poprzednią konfigurację.",
            restarted = true,
            activeBand = active.ActiveBand,
            bandConfirmed = false,
            rolledBack = restored.Item1,
            routerRestored = restored.Item1,
            rollbackDetail = restored.Item2
        }, statusCode: restored.Item1 ? StatusCodes.Status409Conflict : StatusCodes.Status500InternalServerError);
    }
    return Results.Json(new { start.Success, start.Code, start.Message, restarted = true, activeBand = active.ActiveBand, bandConfirmed = confirmed },
        statusCode: StatusCodes.Status200OK);
});

app.MapPost("/api/share/rotate-password", async (IRouterCoordinator coordinator, CancellationToken cancellationToken) =>
{
    var result = await coordinator.RotateSharePasswordAsync(cancellationToken).ConfigureAwait(false);
    return Results.Json(new
    {
        result.Result.Success,
        result.Result.Code,
        result.Result.Message,
        password = result.GeneratedPassword
    }, statusCode: result.Result.Success ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
});

app.MapPost("/api/clients/primary", (PrimaryClientRequest request, IRouterCoordinator coordinator) =>
{
    try
    {
        coordinator.MarkPrimaryClient(request.MacAddress);
        return Results.Ok(new { success = true, code = "ok", message = "Oznaczono laptop firmowy." });
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { success = false, code = "invalid_mac", message = exception.Message });
    }
});

app.MapPost("/api/diagnostics", async (IRouterCoordinator coordinator, CancellationToken cancellationToken) =>
{
    var result = await coordinator.RunDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
    return Results.Json(new
    {
        result.Success,
        passed = result.Success,
        result.Code,
        summary = result.Message,
        details = result.Success
            ? "To jest diagnostyka lokalna. Pełna akceptacja wymaga testu z laptopa firmowego."
            : "Router pozostaje wyłączony albo zostanie wyłączony przez strażnika."
    }, statusCode: result.Success ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
});

app.MapFallbackToFile("index.html");
await app.RunAsync().ConfigureAwait(false);

static IResult ToResult(OperationResult result) => Results.Json(
    result,
    statusCode: result.Success ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);

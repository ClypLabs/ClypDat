#if CLYPDAT_LINUX
using ClypDat.App.Services.LinuxPortal;
using Tmds.DBus.Protocol;

namespace ClypDat.App.Services;

internal sealed class LinuxGlobalShortcuts : IDisposable
{
    internal const string SaveAction = "save-replay", SessionAction = "toggle-full-session";
    private readonly DBusConnection _connection = new(new DBusConnectionOptions(DBusAddress.Session!) { AutoConnect = false });
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly SemaphoreSlim _configuration = new(1, 1);
    private GlobalShortcuts? _portal;
    private ObjectPath? _session;
    private readonly List<IDisposable> _watches = [];
    private bool _bound;
    private readonly string _saveTrigger, _sessionTrigger;
    internal LinuxGlobalShortcuts(string saveTrigger, string sessionTrigger) { _saveTrigger = saveTrigger; _sessionTrigger = sessionTrigger; }
    internal async Task CheckSessionAsync(CancellationToken token) {
        if (_configuration.CurrentCount == 0 || _requests.CurrentCount == 0) return;
        if (_portal is null || _session is null) throw new IOException("KDE shortcut session disconnected.");
        await RequestAsync(options => _portal.ListShortcutsAsync(_session.Value, options), token);
    }
    internal event Action<string>? Activated;
    internal event Action<IReadOnlyDictionary<string, string>>? BindingsChanged;
    internal async Task StartAsync(CancellationToken token)
    {
        await _connection.ConnectAsync();
        await new Registry(_connection, "org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop").RegisterAsync("com.clyplabs.ClypDat", new());
        _portal = new(_connection, "org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop");
        var result = await RequestAsync(options => { options["session_handle_token"] = "clypdat_" + Guid.NewGuid().ToString("N"); return _portal.CreateSessionAsync(options); }, token);
        _session = new ObjectPath(result["session_handle"].GetString());
        _watches.Add(await _portal.WatchActivatedAsync(notification =>
        {
            if (!notification.IsCompletion && notification.Value.SessionHandle == _session) Activated?.Invoke(notification.Value.ShortcutId);
        }, ObserverFlags.None));
        _watches.Add(await _portal.WatchShortcutsChangedAsync(notification =>
        {
            if (!notification.IsCompletion && notification.Value.SessionHandle == _session) Publish(notification.Value.Shortcuts);
        }, ObserverFlags.None));
        // Previously confirmed bindings may be restored without prompting on startup.
        var listed = await RequestAsync(options => _portal.ListShortcutsAsync(_session.Value, options), token);
        if (listed.TryGetValue("shortcuts", out var shortcuts)) {
            Publish(shortcuts);
            if (shortcuts.GetArray<VariantValue>().Length > 0) await ConfigureAsync(string.Empty, token);
        }
    }
    internal async Task ConfigureAsync(string parent, CancellationToken token)
    {
        await _configuration.WaitAsync(token);
        try { await ConfigureCoreAsync(parent, token); }
        finally { _configuration.Release(); }
    }
    private async Task ConfigureCoreAsync(string parent, CancellationToken token)
    {
        if (_portal is null || _session is null) throw new InvalidOperationException("KDE global shortcuts are unavailable. Button saves remain available.");
        if (_bound && await _portal.GetVersionAsync() >= 2)
        { await _portal.ConfigureShortcutsAsync(_session.Value, parent, new()); return; }
        if (_bound) throw new InvalidOperationException("Open KDE System Settings → Shortcuts to change the confirmed bindings.");
        var shortcuts = new (string, Dictionary<string, VariantValue>)[]
        {
            (SaveAction, new() { ["description"] = "Save replay", ["preferred_trigger"] = _saveTrigger }),
            (SessionAction, new() { ["description"] = "Toggle full session recording", ["preferred_trigger"] = _sessionTrigger })
        };
        var response = await RequestAsync(options => _portal.BindShortcutsAsync(_session.Value, shortcuts, parent, options), token);
        _bound = true;
        if (response.TryGetValue("shortcuts", out var value)) Publish(value);
    }
    private void Publish(VariantValue value)
    {
        var bindings = new Dictionary<string, string>();
        foreach (var tuple in value.GetArray<VariantValue>())
        {
            var properties = tuple.GetItem(1).GetDictionary<string, VariantValue>();
            if (properties.TryGetValue("trigger_description", out var trigger)) bindings[tuple.GetItem(0).GetString()] = trigger.GetString();
        }
        BindingsChanged?.Invoke(bindings);
    }
    private void Publish((string, Dictionary<string, VariantValue>)[] shortcuts) =>
        BindingsChanged?.Invoke(shortcuts.Where(s => s.Item2.ContainsKey("trigger_description"))
            .ToDictionary(s => s.Item1, s => s.Item2["trigger_description"].GetString()));
    private async Task<Dictionary<string, VariantValue>> RequestAsync(Func<Dictionary<string, VariantValue>, Task<ObjectPath>> send, CancellationToken token)
    {
        await _requests.WaitAsync(token);
        try
        {
            var handle = "clypdat_" + Guid.NewGuid().ToString("N");
            var path = new ObjectPath("/org/freedesktop/portal/desktop/request/" + (_connection.UniqueName ?? "").TrimStart(':').Replace('.', '_') + "/" + handle);
            var request = new Request(_connection, "org.freedesktop.portal.Desktop", path);
            var completion = new TaskCompletionSource<Dictionary<string, VariantValue>>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watch = await request.WatchResponseAsync(notification =>
            {
                if (notification.IsCompletion) completion.TrySetException(notification.Exception);
                else if (notification.Value.Response != 0) completion.TrySetException(new InvalidOperationException("KDE shortcut setup was cancelled or denied."));
                else completion.TrySetResult(notification.Value.Results);
            }, ObserverFlags.None);
            var actual = await send(new() { ["handle_token"] = handle });
            if (actual != path) throw new InvalidOperationException("KDE portal returned an unexpected request handle.");
            try { return await completion.Task.WaitAsync(token); }
            catch (OperationCanceledException) { await request.CloseAsync(); throw; }
        }
        finally { _requests.Release(); }
    }
    public void Dispose() { foreach (var watch in _watches) watch.Dispose(); _connection.Dispose(); }
}
#endif

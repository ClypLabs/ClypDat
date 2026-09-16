using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ClypDat.App.Services;

/// <summary>What ClypDat is currently doing, as Discord should describe it.</summary>
internal sealed record DiscordPresence(
    string Details,
    string State,
    DateTime? StartedUtc,
    string? LargeImageUrl = null,
    string? LargeImageText = null,
    string? LargeImageLink = null,
    string? SmallImageUrl = null,
    string? SmallImageText = null)
{
    public static readonly DiscordPresence None = new(string.Empty, string.Empty, null);

    public bool IsEmpty => Details.Length == 0 && State.Length == 0;
}

// Discord Rich Presence, spoken directly over Discord's local IPC pipe.
//
// No library: the protocol is a length-prefixed JSON frame on a named pipe,
// and the official C# wrappers all ship native binaries that would need
// bundling, signing and per-architecture handling for something that is about
// forty lines of framing. Everything here is managed and in-process.
//
// The whole feature is dormant until an application id is configured. Without
// one Discord has no name or artwork to show, so a half-configured install
// broadcasts nothing at all rather than an anonymous "unknown game".
internal static class DiscordRichPresenceService
{
    // Discord listens on discord-ipc-0 and moves up as instances stack (a
    // second client, Canary alongside stable). Trying all ten costs nothing
    // when the first answers, and is the difference between working and not
    // for anyone running more than one Discord build.
    private const int MaxPipeIndex = 9;

    // Discord allows up to two buttons; one is enough. Note that Discord
    // deliberately hides activity buttons on your OWN profile - they render
    // for everyone else, so "it does not show up for me" is expected rather
    // than a fault.
    private const string ButtonLabel = "Get ClypDat";
    private const string ButtonUrl = "https://www.clypdat.xyz";
    /// <summary>
    /// ClypDat's own Discord application - the identity the status is published
    /// under. Fixed rather than configurable: pointing it at another
    /// application would show that application's name and artwork while still
    /// broadcasting ClypDat's activity, which is not something to leave as a
    /// text box. Not a secret either way; every client showing the presence
    /// receives it in the clear.
    /// </summary>
    private const string ApplicationId = "1542340384418439189";

    /// <summary>
    /// The twin application carrying the original hexagon mark. Discord takes
    /// both the presence artwork and the name printed beside it from whichever
    /// application the connection handshook as, so showing the old logo means
    /// connecting as a different application - there is no per-activity
    /// override for either. Its Rich Presence assets have to include one named
    /// "clypdat", since that is the key CreateAssets sends when there is no
    /// game art to show.
    ///
    /// An empty id falls back to the current application, so a missing twin
    /// leaves the presence working rather than breaking the connection.
    /// </summary>
    private const string ClassicApplicationId = "1549768116165279774";

    private static bool _useClassicApplication;

    private static string ActiveApplicationId => ResolveApplicationId(_useClassicApplication);

    internal static string ResolveApplicationId(bool classicLogo) =>
        classicLogo && ClassicApplicationId.Length > 0 ? ClassicApplicationId : ApplicationId;

    // SET_ACTIVITY is rate limited by Discord to roughly five updates per
    // twenty seconds. Updates are coalesced to comfortably inside that: going
    // over does not error, it silently drops the update, which would leave a
    // stale status with nothing in the log to explain it.
    private static readonly TimeSpan MinimumUpdateInterval = TimeSpan.FromSeconds(5);

    // Discord not running is the normal case, not a failure - retry quietly
    // and indefinitely rather than giving up on the first miss.
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(30);

    // How long a new worker generation waits for the previous one to unwind
    // before starting anyway. Cancellation normally lands in well under a
    // second; this only exists so a pipe wedged in a driver-level read cannot
    // keep Rich Presence off forever.
    private static readonly TimeSpan WorkerDrainTimeout = TimeSpan.FromSeconds(5);

    private static readonly object Sync = new();
    private static readonly SemaphoreSlim Wake = new(0, 1);

    private static bool _enabled;
    private static bool _showGetClypDatButton;
    // Presence and button state form one activity revision. Keeping button
    // state separately let an in-flight write acknowledge a newer toggle as
    // already sent, leaving Discord with the old button until another activity
    // change happened.
    private sealed record ActivityRevision(DiscordPresence Presence, bool ShowButton, long Number);
    private static long _revisionNumber;
    private static ActivityRevision _desired = new(DiscordPresence.None, false, 0);
    private static ActivityRevision? _sent;
    private static Task? _worker;
    // Kept after Stop clears _worker, so the next generation has something to
    // wait on. Cleared nowhere: a completed task is free to await.
    private static Task? _previousWorker;
    private static CancellationTokenSource? _cts;

    /// <summary>
    /// Applies the user's settings. Safe to call on every settings save: it
    /// only restarts the worker when something it actually depends on changed.
    /// </summary>
    public static void Configure(bool enabled, bool showGetClypDatButton, bool classicLogo)
    {
        var enabledChanged = false;
        var buttonChanged = false;
        var applicationChanged = false;
        lock (Sync)
        {
            enabledChanged = _enabled != enabled;
            buttonChanged = _showGetClypDatButton != showGetClypDatButton;
            applicationChanged = _useClassicApplication != classicLogo && ClassicApplicationId.Length > 0;
            if (!enabledChanged && !buttonChanged && !applicationChanged) return;
            _enabled = enabled;
            _showGetClypDatButton = showGetClypDatButton;
            _useClassicApplication = classicLogo;

            // Same activity needs sending again when only its Discord button
            // changes; otherwise SetPresence correctly coalesces it away.
            if (buttonChanged)
                _desired = new(_desired.Presence, showGetClypDatButton, ++_revisionNumber);
        }

        if (!enabled)
        {
            Stop();
            return;
        }

        // An application change is NOT a restart. Stopping and starting left
        // the outgoing worker alive inside its own connect or pump - Stop only
        // cancels, it does not wait - so a fast old/new/old flip ran two or
        // three of them at once against the same pid. Whichever one lost the
        // race disposed its pipe last, and Discord clears a pid's activity when
        // a connection closes, so the status went blank while the app believed
        // it had just published one. The worker owns its identity instead: it
        // notices the id it handshook with is stale and reconnects in place.
        if (enabledChanged) Start();
        try { Wake.Release(); } catch (SemaphoreFullException) { }
    }

    /// <summary>
    /// Records what should be shown. Cheap and non-blocking - the worker owns
    /// the pipe and picks this up on its own schedule, so callers can push a
    /// new presence from any thread as often as state changes.
    /// </summary>
    public static void SetPresence(DiscordPresence presence)
    {
        lock (Sync)
        {
            if (_desired.Presence == presence) return;
            _desired = new ActivityRevision(presence, _showGetClypDatButton, ++_revisionNumber);
        }

        // Never blocks: the semaphore is a signal that work exists, and one
        // pending signal is as good as ten.
        try { Wake.Release(); } catch (SemaphoreFullException) { }
    }

    public static void Shutdown() => Stop();

    private static void Start()
    {
        lock (Sync)
        {
            if (_worker is not null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            // Chained onto whatever the last generation is still doing. Stop
            // cancels without waiting, so its worker can be mid-handshake when
            // this one is created, and two live pipes from one pid is how the
            // presence ends up showing the wrong application or nothing at all.
            var previous = _previousWorker;
            _worker = _previousWorker = Task.Run(async () =>
            {
                if (previous is not null)
                {
                    try { await previous.WaitAsync(WorkerDrainTimeout).ConfigureAwait(false); }
                    catch { /* a wedged previous generation must not block this one forever */ }
                }

                await RunAsync(token).ConfigureAwait(false);
            });
        }

        AppLog.Info("Discord Rich Presence: enabled.");
    }

    private static void Stop(string reason = "disabled")
    {
        CancellationTokenSource? cts;
        lock (Sync)
        {
            cts = _cts;
            _cts = null;
            _worker = null;
            _sent = null;
        }

        if (cts is null) return;
        try { cts.Cancel(); } catch { /* teardown is best effort */ }
        try { cts.Dispose(); } catch { /* teardown is best effort */ }
        AppLog.Info($"Discord Rich Presence: {reason}.");
    }

    private static async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeClientStream? pipe = null;
            // Read once per connection: the handshake fixes the identity for
            // the life of the pipe, so this is what the pump compares against.
            var applicationId = ActiveApplicationId;
            var switching = false;
            try
            {
                pipe = await ConnectAsync(applicationId, cancellationToken).ConfigureAwait(false);
                if (pipe is null)
                {
                    // Discord is not running. Nothing is wrong; wait and look
                    // again.
                    await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                lock (Sync) _sent = null;
                switching = await PumpAsync(pipe, applicationId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception error)
            {
                // Discord quitting mid-session lands here. It is expected
                // often enough that it is not worth an error-level entry every
                // time somebody closes Discord.
                AppLog.Info($"Discord Rich Presence: connection lost ({error.GetType().Name}); retrying.");
            }
            finally
            {
                try { pipe?.Dispose(); } catch { /* teardown is best effort */ }
            }

            if (cancellationToken.IsCancellationRequested) break;

            // The delay is there for "Discord is not running", which is worth
            // being patient about. A logo swap is a user standing in front of
            // the app waiting to see it change, so that reconnect goes now.
            if (switching)
            {
                AppLog.Info($"Discord Rich Presence: reconnecting as the {(ActiveApplicationId == ApplicationId ? "current" : "classic")} application.");
                continue;
            }

            try { await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static async Task<NamedPipeClientStream?> ConnectAsync(string applicationId, CancellationToken cancellationToken)
    {
        for (var index = 0; index <= MaxPipeIndex; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{index}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                // Short timeout per index: a missing pipe should fail fast so
                // the next one is tried, not stall the loop for ten seconds.
                await pipe.ConnectAsync(300, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                pipe.Dispose();
                throw;
            }
            catch
            {
                pipe.Dispose();
                continue;
            }

            try
            {
                await WriteFrameAsync(pipe, Opcode.Handshake,
                    JsonSerializer.Serialize(new { v = 1, client_id = applicationId }), cancellationToken).ConfigureAwait(false);

                // Read the reply here, before anything else uses the pipe.
                // Writing the handshake only proves the pipe accepted bytes -
                // Discord still has to answer READY, and doing that read on
                // this thread means a refusal is seen immediately instead of
                // looking like a healthy connection that never shows anything.
                var reply = await ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false);
                if (reply is null || !IsReady(reply))
                {
                    AppLog.Error($"Discord Rich Presence: handshake refused on discord-ipc-{index}: {Shorten(reply ?? "no reply")}");
                    pipe.Dispose();
                    continue;
                }

                AppLog.Info($"Discord Rich Presence: connected on discord-ipc-{index}.");
                return pipe;
            }
            catch (Exception error)
            {
                AppLog.Info($"Discord Rich Presence: handshake failed on discord-ipc-{index} ({error.GetType().Name}).");
                pipe.Dispose();
            }
        }

        return null;
    }

    /// <summary>
    /// Returns true when the pump stopped because the presence identity
    /// changed under it, which the caller answers with an immediate reconnect
    /// rather than the usual backoff.
    /// </summary>
    private static async Task<bool> PumpAsync(NamedPipeClientStream pipe, string applicationId, CancellationToken cancellationToken)
    {
        // Reads are drained but ignored. Discord answers every frame, and a
        // pipe whose read buffer is never emptied eventually blocks the writer.
        var drain = Task.Run(() => DrainAsync(pipe, cancellationToken), cancellationToken);

        while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
        {
            if (ActiveApplicationId != applicationId)
            {
                await drain.ConfigureAwait(false);
                return true;
            }

            // Null means "nothing changed". Resolved under the lock, sent
            // outside it - a pipe write must never be held across a lock the
            // UI thread also takes to push a new presence.
            ActivityRevision? toSend;
            lock (Sync) toSend = _desired == _sent ? null : _desired;

            if (toSend is not null)
            {
                await SendActivityAsync(pipe, toSend, cancellationToken).ConfigureAwait(false);
                lock (Sync) _sent = toSend;
            }

            // Woken early by SetPresence, otherwise polls at the rate limit -
            // which also serves as the keepalive that notices a dead pipe.
            try { await Wake.WaitAsync(MinimumUpdateInterval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        await drain.ConfigureAwait(false);
        return false;
    }

    private static async Task SendActivityAsync(NamedPipeClientStream pipe, ActivityRevision revision, CancellationToken cancellationToken)
    {
        var activity = CreateActivity(revision.Presence, revision.ShowButton);

        var payload = JsonSerializer.Serialize(new
        {
            cmd = "SET_ACTIVITY",
            nonce = Guid.NewGuid().ToString("N"),
            args = new { pid = Environment.ProcessId, activity }
        });

        await WriteFrameAsync(pipe, Opcode.Frame, payload, cancellationToken).ConfigureAwait(false);
    }

    internal static object? CreateActivity(DiscordPresence presence, bool showGetClypDatButton)
    {
        // Clearing the status requires an explicit null activity.
        if (!presence.IsEmpty)
        {
            var fields = new Dictionary<string, object?>
            {
                ["type"] = 0,
                // Discord otherwise uses the registered application name
                // ("ClypDat") in compact member/friend-list status text.
                ["status_display_type"] = 2,
                ["timestamps"] = presence.StartedUtc is { } started
                    ? new { start = new DateTimeOffset(DateTime.SpecifyKind(started, DateTimeKind.Utc)).ToUnixTimeSeconds() }
                    : null,
                ["assets"] = CreateAssets(presence)
            };

            if (Trim(presence.Details) is { } details) fields["details"] = details;
            if (Trim(presence.State) is { } state) fields["state"] = state;
            // Omitting buttons, rather than serializing buttons: null, gives
            // Discord an unambiguous activity update that removes old buttons.
            if (showGetClypDatButton)
                fields["buttons"] = new[] { new { label = ButtonLabel, url = ButtonUrl } };
            return fields;
        }

        return null;
    }

    private static Dictionary<string, string> CreateAssets(DiscordPresence presence)
    {
        Dictionary<string, string> assets;
        if (IsExternalImageUrl(presence.LargeImageUrl))
        {
            assets = new Dictionary<string, string>
            {
                ["large_image"] = presence.LargeImageUrl!,
                ["large_text"] = presence.LargeImageText is { } text ? Trim(text) ?? "Current game" : "Current game"
            };
            if (!string.IsNullOrWhiteSpace(presence.LargeImageLink)) assets["large_url"] = presence.LargeImageLink;
        }
        else
        {
            assets = new Dictionary<string, string>
            {
                ["large_image"] = "clypdat",
                ["large_text"] = "ClypDat"
            };
        }

        // The corner badge: the champion or hero being played.
        if (IsExternalImageUrl(presence.SmallImageUrl))
        {
            assets["small_image"] = presence.SmallImageUrl!;
            if (presence.SmallImageText is { } smallText && Trim(smallText) is { } trimmed) assets["small_text"] = trimmed;
        }
        return assets;
    }

    private static bool IsExternalImageUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    // Discord rejects a details or state string longer than 128 bytes, and
    // rejects one shorter than two characters - a one-character game name
    // would fail the whole update rather than just that field.
    private static string? Trim(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length < 2) trimmed += " ";
        return trimmed.Length <= 128 ? trimmed : trimmed[..128];
    }

    private static async Task WriteFrameAsync(NamedPipeClientStream pipe, Opcode opcode, string payload, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        var frame = new byte[8 + body.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), (int)opcode);
        BitConverter.TryWriteBytes(frame.AsSpan(4, 4), body.Length);
        body.CopyTo(frame.AsSpan(8));
        await pipe.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DrainAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        try
        {
            while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
            {
                if (!await ReadExactlyAsync(pipe, header, cancellationToken).ConfigureAwait(false)) return;
                var length = BitConverter.ToInt32(header, 4);
                if (length <= 0 || length > 64 * 1024) return;
                var body = new byte[length];
                if (!await ReadExactlyAsync(pipe, body, cancellationToken).ConfigureAwait(false)) return;
                LogReply(Encoding.UTF8.GetString(body));
            }
        }
        catch (Exception error)
        {
            // Not silent: a read that dies here is exactly the failure that
            // made a connected-but-invisible presence impossible to explain.
            if (!cancellationToken.IsCancellationRequested)
            {
                AppLog.Info($"Discord Rich Presence: reply stream ended ({error.GetType().Name}).");
            }
        }
    }

    // Replies are the only place Discord ever explains a refusal - an unknown
    // application id, a malformed activity and a working connection all look
    // identical from the writing side, which is why "connected" was logged
    // while nothing appeared on the profile.
    private static void LogReply(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var evt = root.TryGetProperty("evt", out var evtElement) ? evtElement.GetString() : null;

            if (string.Equals(evt, "ERROR", StringComparison.Ordinal))
            {
                var code = root.TryGetProperty("data", out var errorData) && errorData.TryGetProperty("code", out var codeElement)
                    ? codeElement.GetRawText()
                    : "?";
                var message = root.TryGetProperty("data", out var messageData) && messageData.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : json;
                AppLog.Error($"Discord Rich Presence rejected (code {code}): {message}");
                return;
            }

            if (string.Equals(evt, "READY", StringComparison.Ordinal))
            {
                AppLog.Info("Discord Rich Presence: handshake accepted.");
                return;
            }

            AppLog.Debug($"Discord Rich Presence reply: {Shorten(json)}");
        }
        catch
        {
            AppLog.Debug($"Discord Rich Presence reply (unparsed): {Shorten(json)}");
        }
    }

    private static string Shorten(string value) => value.Length <= 400 ? value : value[..400] + "...";

    private static bool IsReady(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("evt", out var evt)
                   && string.Equals(evt.GetString(), "READY", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reads one whole frame, or null when the pipe ends.</summary>
    private static async Task<string?> ReadFrameAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        if (!await ReadExactlyAsync(pipe, header, cancellationToken).ConfigureAwait(false)) return null;
        var length = BitConverter.ToInt32(header, 4);
        if (length <= 0 || length > 64 * 1024) return null;
        var body = new byte[length];
        if (!await ReadExactlyAsync(pipe, body, cancellationToken).ConfigureAwait(false)) return null;
        return Encoding.UTF8.GetString(body);
    }

    private static async Task<bool> ReadExactlyAsync(NamedPipeClientStream pipe, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await pipe.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read <= 0) return false;
            offset += read;
        }

        return true;
    }

    private enum Opcode
    {
        Handshake = 0,
        Frame = 1,
        Close = 2,
        Ping = 3,
        Pong = 4
    }
}

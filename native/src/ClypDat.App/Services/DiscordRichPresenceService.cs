using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ClypDat.App.Services;

/// <summary>What ClypDat is currently doing, as Discord should describe it.</summary>
internal sealed record DiscordPresence(string Details, string State, DateTime? StartedUtc)
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

    // SET_ACTIVITY is rate limited by Discord to roughly five updates per
    // twenty seconds. Updates are coalesced to comfortably inside that: going
    // over does not error, it silently drops the update, which would leave a
    // stale status with nothing in the log to explain it.
    private static readonly TimeSpan MinimumUpdateInterval = TimeSpan.FromSeconds(5);

    // Discord not running is the normal case, not a failure - retry quietly
    // and indefinitely rather than giving up on the first miss.
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(30);

    private static readonly object Sync = new();
    private static readonly SemaphoreSlim Wake = new(0, 1);

    private static bool _enabled;
    private static bool _showGetClypDatButton;
    private static DiscordPresence _desired = DiscordPresence.None;
    private static DiscordPresence? _sent;
    private static Task? _worker;
    private static CancellationTokenSource? _cts;

    /// <summary>
    /// Applies the user's settings. Safe to call on every settings save: it
    /// only restarts the worker when something it actually depends on changed.
    /// </summary>
    public static void Configure(bool enabled, bool showGetClypDatButton)
    {
        var enabledChanged = false;
        var buttonChanged = false;
        lock (Sync)
        {
            enabledChanged = _enabled != enabled;
            buttonChanged = _showGetClypDatButton != showGetClypDatButton;
            if (!enabledChanged && !buttonChanged) return;
            _enabled = enabled;
            _showGetClypDatButton = showGetClypDatButton;

            // Same activity needs sending again when only its Discord button
            // changes; otherwise SetPresence correctly coalesces it away.
            if (buttonChanged) _sent = null;
        }

        if (!enabled)
        {
            Stop();
            return;
        }

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
            if (_desired == presence) return;
            _desired = presence;
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
            _worker = Task.Run(() => RunAsync(token), token);
        }

        AppLog.Info("Discord Rich Presence: enabled.");
    }

    private static void Stop()
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
        AppLog.Info("Discord Rich Presence: disabled.");
    }

    private static async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeClientStream? pipe = null;
            try
            {
                pipe = await ConnectAsync(cancellationToken).ConfigureAwait(false);
                if (pipe is null)
                {
                    // Discord is not running. Nothing is wrong; wait and look
                    // again.
                    await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                lock (Sync) _sent = null;
                await PumpAsync(pipe, cancellationToken).ConfigureAwait(false);
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
            try { await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static async Task<NamedPipeClientStream?> ConnectAsync(CancellationToken cancellationToken)
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
                    JsonSerializer.Serialize(new { v = 1, client_id = ApplicationId }), cancellationToken).ConfigureAwait(false);

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

    private static async Task PumpAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        // Reads are drained but ignored. Discord answers every frame, and a
        // pipe whose read buffer is never emptied eventually blocks the writer.
        var drain = Task.Run(() => DrainAsync(pipe, cancellationToken), cancellationToken);

        while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
        {
            // Null means "nothing changed". Resolved under the lock, sent
            // outside it - a pipe write must never be held across a lock the
            // UI thread also takes to push a new presence.
            DiscordPresence? toSend;
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
    }

    private static async Task SendActivityAsync(NamedPipeClientStream pipe, DiscordPresence presence, CancellationToken cancellationToken)
    {
        var activity = CreateActivity(presence, _showGetClypDatButton);

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
                ["assets"] = new { large_image = "clypdat", large_text = "ClypDat" },
                ["buttons"] = showGetClypDatButton
                    ? new[] { new { label = ButtonLabel, url = ButtonUrl } }
                    : null
            };

            if (Trim(presence.Details) is { } details) fields["details"] = details;
            if (Trim(presence.State) is { } state) fields["state"] = state;
            return fields;
        }

        return null;
    }

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

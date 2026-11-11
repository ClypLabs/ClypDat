using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClypDat.App.Services;

public enum NightlyStepState { Pending, Running, Done, Skipped, Failed }

public sealed record NightlyStep(string Id, string Label, NightlyStepState State, double? Progress, string Detail);

public sealed record NightlyReport(
    IReadOnlyList<NightlyStep> Steps,
    bool Running,
    bool Finished,
    string? Error,
    string? Note,
    string? LogPath,
    string? Commit);

public sealed record NightlyChange(string Kind, string Text);

public sealed record NightlyUpdate(string Commit, IReadOnlyList<NightlyChange> Changes);

public static class NightlyBuild
{
    private const string Owner = "ClypLabs";
    private const string Repository = "ClypDat";
    private const string Branch = "master";
    private const string AppRemote = "https://github.com/ClypLabs/ClypDat.git";
    private const string AppRemoteFallback = "https://gitlab.com/clypdat-group1/ClypDat-App.git";
    private const string AvaloniaRemote = "https://github.com/ClypLabs/clypdat-avalonia.git";
    private const string AvaloniaRemoteFallback = "https://gitlab.com/clypdat-group1/clypdat-avalonia.git";
    private const string GitLabBranchUrl = "https://gitlab.com/api/v4/projects/clypdat-group1%2FClypDat-App/repository/branches/master";

    // Only used when no Git is installed.
    private const string MinGitUrl = "https://github.com/git-for-windows/git/releases/download/v2.56.1.windows.1/MinGit-2.56.1-64-bit.zip";
    private const string MinGitSha256 = "4a92850b6e917da4fd010669b614b55dccf5b0ec333a534cc4be4700dfefdafd";

    private const string VsBootstrapperUrl = "https://aka.ms/vs/17/release/vs_BuildTools.exe";
    private static readonly string[] VsComponents =
    [
        "Microsoft.VisualStudio.Workload.VCTools",
        "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
        "Microsoft.VisualStudio.Component.Windows11SDK.26100",
        "Microsoft.VisualStudio.Component.VC.CMake.Project"
    ];

    private const long GB = 1_000_000_000;
    private const long BuildSpace = 20 * GB;
    private const long VsSpace = 8 * GB;
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(10);
    private const int ChangesShown = 30;
    private const int AvaloniaPackageCount = 25;

    private static readonly object Gate = new();
    private static NightlyReport? _last;
    private static CancellationTokenSource? _cancel;
    private static Process? _tool;
    private static bool _clearing;

    public static event Action<NightlyReport>? Changed;

    public static string? Commit { get; } = Assembly.GetEntryAssembly()?
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "ClypDatNightlyCommit")?.Value is { Length: > 0 } commit ? commit : null;

    public static bool IsNightlyBuild => Commit is not null;

    public static bool ChannelIsNightly { get; set; }

    public static bool ReturningToStable => IsNightlyBuild && !ChannelIsNightly;

    public static NightlyUpdate? LastFound { get; set; }

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClypDat", "Nightly");

    private static string SourceRoot => Path.Combine(Root, "src");
    private static string AppSource => Path.Combine(SourceRoot, "clypdat-app");
    private static string AvaloniaSource => Path.Combine(SourceRoot, "clypdat-avalonia");
    private static string Staged => Path.Combine(Root, "staged");
    private static string ReadyFile => Path.Combine(Staged, "nightly-ready.json");
    private static string Downloads => Path.Combine(Root, "downloads");
    private static string Tools => Path.Combine(Root, "tools");
    private static string Logs => Path.Combine(Root, "logs");

    public static NightlyReport? Last { get { lock (Gate) return _last; } }

    public static bool IsRunning { get { lock (Gate) return _cancel is not null; } }

    public static string? ReadyCommit
    {
        get
        {
            try
            {
                if (!File.Exists(ReadyFile) || !File.Exists(Path.Combine(Staged, "ClypDat.exe"))) return null;
                using var document = JsonDocument.Parse(File.ReadAllText(ReadyFile));
                var commit = document.RootElement.GetProperty("commit").GetString();
                return commit is { Length: > 0 } && !string.Equals(commit, Commit, StringComparison.OrdinalIgnoreCase) ? commit : null;
            }
            catch { return null; }
        }
    }

    public static bool Start()
    {
        CancellationToken token;
        lock (Gate)
        {
            if (_cancel is not null || _clearing) return false;
            _cancel = new CancellationTokenSource();
            token = _cancel.Token;
        }
        var thread = new Thread(() =>
        {
            try { new Run(token).Execute(); }
            finally
            {
                lock (Gate)
                {
                    _cancel?.Dispose();
                    _cancel = null;
                    _tool = null;
                }
            }
        }) { IsBackground = true, Name = "Nightly build", Priority = ThreadPriority.BelowNormal };
        thread.Start();
        return true;
    }

    public static void Cancel()
    {
        lock (Gate)
        {
            _cancel?.Cancel();
            try { _tool?.Kill(entireProcessTree: true); }
            catch { }
        }
    }

    public static bool InstallReady()
    {
        if (ReadyCommit is null) return false;
        var script = Path.Combine(AppSource, "build.ps1");
        if (!File.Exists(script)) return false;
        var start = new ProcessStartInfo(PowerShell(), $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -InstallFrom \"{Staged}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppSource
        };
        PrepareEnvironment(start, FindGit());
        Process.Start(start);
        AppLog.Info($"Nightly: installing {ReadyCommit} and restarting.");
        return true;
    }

    public static long ClearCache()
    {
        lock (Gate)
        {
            if (_cancel is not null) throw new InvalidOperationException("A Nightly build is running.");
            _clearing = true;
        }
        try
        {
            long freed = 0;
            foreach (var directory in new[] { Staged, Downloads, Logs, Path.Combine(AvaloniaSource) })
                freed += DeleteDirectory(directory);
            if (Directory.Exists(AppSource))
                foreach (var entry in Directory.EnumerateFileSystemEntries(AppSource))
                    if (!Path.GetFileName(entry).Equals(".dotnet", StringComparison.OrdinalIgnoreCase))
                        freed += Directory.Exists(entry) ? DeleteDirectory(entry) : DeleteFile(entry);
            lock (Gate) _last = null;
            return freed;
        }
        finally { lock (Gate) _clearing = false; }
    }

    public static long CacheSize() => FolderSize(Root);

    public static async Task<NightlyUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var client = Client();
        var latest = await LatestCommitAsync(client, cancellationToken);
        if (latest is null || string.Equals(latest, Commit, StringComparison.OrdinalIgnoreCase)) return null;
        var changes = Commit is null ? [] : await ChangesAsync(client, Commit, latest, cancellationToken);
        return new NightlyUpdate(latest, changes);
    }

    private static async Task<string?> LatestCommitAsync(HttpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Owner}/{Repository}/commits/{Branch}");
            request.Headers.Accept.ParseAdd("application/vnd.github.sha");
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var sha = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
                if (IsCommit(sha)) return sha;
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            AppLog.Info($"Nightly: GitHub commit check failed ({error.GetType().Name}); trying GitLab.");
        }
        try
        {
            using var document = JsonDocument.Parse(await client.GetStringAsync(GitLabBranchUrl, cancellationToken));
            var sha = document.RootElement.GetProperty("commit").GetProperty("id").GetString();
            return sha is not null && IsCommit(sha) ? sha : null;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException && !cancellationToken.IsCancellationRequested)
        {
            AppLog.Info($"Nightly: GitLab commit check failed ({error.GetType().Name}).");
            return null;
        }
    }

    private static async Task<IReadOnlyList<NightlyChange>> ChangesAsync(HttpClient client, string current, string latest, CancellationToken cancellationToken)
    {
        try
        {
            var json = await client.GetStringAsync($"https://api.github.com/repos/{Owner}/{Repository}/compare/{current}...{latest}", cancellationToken);
            using var document = JsonDocument.Parse(json);
            return ParseChanges(document.RootElement);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            AppLog.Info($"Nightly: change list unavailable ({error.GetType().Name}).");
            return [];
        }
    }

    internal static IReadOnlyList<NightlyChange> ParseChanges(JsonElement comparison)
    {
        if (!comparison.TryGetProperty("commits", out var commits) || commits.ValueKind != JsonValueKind.Array) return [];
        var changes = new List<NightlyChange>();
        foreach (var item in commits.EnumerateArray().Reverse())
        {
            if (item.TryGetProperty("parents", out var parents) && parents.GetArrayLength() > 1) continue;
            var message = item.GetProperty("commit").GetProperty("message").GetString() ?? string.Empty;
            if (Change(message) is { } change) changes.Add(change);
            if (changes.Count == ChangesShown) break;
        }
        return changes;
    }

    internal static NightlyChange? Change(string message)
    {
        var lines = message.Replace("\r", string.Empty).Split('\n');
        var subject = lines[0].Trim();
        if (subject.Length == 0) return null;
        var colon = subject.IndexOf(':');
        var type = colon > 0 ? subject[..colon].Split('(')[0].TrimEnd('!').Trim().ToLowerInvariant() : string.Empty;
        var kind = type switch { "feat" => "new", "fix" or "perf" => "fixed", _ => "other" };
        var trailer = lines.LastOrDefault(line => line.StartsWith("Nightly:", StringComparison.OrdinalIgnoreCase));
        var text = trailer is not null
            ? trailer["Nightly:".Length..].Trim()
            : colon > 0 && type.Length > 0 && type.All(char.IsLetter) ? subject[(colon + 1)..].Trim() : subject;
        if (text.Length == 0) return null;
        return new NightlyChange(kind, char.ToUpperInvariant(text[0]) + text[1..]);
    }

    private static bool IsCommit(string text) => text.Length == 40 && text.All(Uri.IsHexDigit);

    private static HttpClient Client()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClypDat-Nightly");
        return client;
    }


    private sealed class Run(CancellationToken token)
    {
        private readonly List<NightlyStep> _steps =
        [
            new("space", "Free space", NightlyStepState.Pending, null, string.Empty),
            new("cpp", "Visual Studio C++ build tools", NightlyStepState.Pending, null, string.Empty),
            new("git", "Git", NightlyStepState.Pending, null, string.Empty),
            new("source", "ClypDat source", NightlyStepState.Pending, null, string.Empty),
            new("dotnet", ".NET SDK", NightlyStepState.Pending, null, string.Empty),
            new("avalonia", "Interface framework", NightlyStepState.Pending, null, string.Empty),
            new("app", "ClypDat", NightlyStepState.Pending, null, string.Empty),
            new("check", "Checking the build", NightlyStepState.Pending, null, string.Empty)
        ];
        private string? _error;
        private string? _note;
        private string? _commit;
        private string? _log;
        private StreamWriter? _logWriter;
        private bool _finished;

        public void Execute()
        {
            Directory.CreateDirectory(Logs);
            _log = Path.Combine(Logs, $"nightly-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            try
            {
                _logWriter = new StreamWriter(_log, append: false, Encoding.UTF8) { AutoFlush = true };
                Publish(running: true);
                CheckSpace();
                var git = EnsureVisualStudioThenGit();
                var commit = FetchSource(git);
                _commit = commit;
                if (string.Equals(commit, Commit, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var id in new[] { "dotnet", "avalonia", "app", "check" }) Set(id, NightlyStepState.Skipped);
                    _note = "This is already the newest Nightly.";
                }
                else if (string.Equals(commit, ReadyCommit, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var id in new[] { "dotnet", "avalonia", "app", "check" }) Set(id, NightlyStepState.Skipped);
                }
                else
                {
                    EnsureDotNet(git);
                    BuildApp(git, commit);
                }
                _finished = true;
                AppLog.Info($"Nightly: build of {commit} finished.");
            }
            catch (OperationCanceledException)
            {
                _error = "Cancelled.";
                FailRunning();
            }
            catch (Exception error)
            {
                _error = error.Message;
                FailRunning();
                AppLog.Error("Nightly build failed", error);
                Write($"ERROR: {error}");
            }
            finally
            {
                Publish(running: false);
                _logWriter?.Dispose();
            }
        }

        private void CheckSpace()
        {
            Set("space", NightlyStepState.Running);
            Directory.CreateDirectory(Root);
            var drive = new DriveInfo(Path.GetPathRoot(Root)!);
            var needed = Directory.Exists(AppSource) ? BuildSpace / 2 : BuildSpace;
            if (drive.AvailableFreeSpace < needed)
                throw new InvalidOperationException(
                    $"Nightly needs about {needed / GB} GB free on {drive.Name.TrimEnd('\\')}; {drive.AvailableFreeSpace / GB} GB is free.");
            Set("space", NightlyStepState.Done);
        }

        private string EnsureVisualStudioThenGit()
        {
            Set("cpp", NightlyStepState.Running);
            if (HasVisualStudio()) Set("cpp", NightlyStepState.Skipped);
            else
            {
                InstallVisualStudio();
                if (!HasVisualStudio())
                    throw new InvalidOperationException("Visual Studio's C++ build tools are still missing after installing them.");
                Set("cpp", NightlyStepState.Done);
            }

            Set("git", NightlyStepState.Running);
            if (FindGit() is { } git)
            {
                Set("git", NightlyStepState.Skipped);
                return git;
            }
            InstallMinGit();
            git = FindGit() ?? throw new InvalidOperationException("Git could not be set up.");
            Set("git", NightlyStepState.Done);
            return git;
        }

        private void InstallVisualStudio()
        {
            var systemDrive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            if (systemDrive.AvailableFreeSpace < VsSpace)
                throw new InvalidOperationException(
                    $"Visual Studio's C++ build tools need about {VsSpace / GB} GB free on {systemDrive.Name.TrimEnd('\\')}.");
            var bootstrapper = Path.Combine(Downloads, "vs_BuildTools.exe");
            Download(VsBootstrapperUrl, bootstrapper, "cpp", sha256: null);
            if (!AuthenticodeCheck.IsMicrosoftSigned(bootstrapper))
                throw new InvalidOperationException("The Visual Studio installer was not signed by Microsoft; it was not run.");
            Set("cpp", NightlyStepState.Running, null, "Windows asks for permission, then Visual Studio's installer shows its own progress.");
            var arguments = "--wait --passive --norestart --nocache " + string.Join(" ", VsComponents.Select(component => $"--add {component}"));
            Write($"> vs_BuildTools.exe {arguments}");
            Process process;
            try
            {
                process = Process.Start(new ProcessStartInfo(bootstrapper, arguments) { UseShellExecute = true, Verb = "runas" })
                          ?? throw new InvalidOperationException("Visual Studio's installer did not start.");
            }
            catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
            {
                throw new InvalidOperationException("Permission to install Visual Studio's C++ build tools was declined.");
            }
            using (process)
            {
                while (!process.WaitForExit(500)) token.ThrowIfCancellationRequested();
                Write($"vs_BuildTools.exe exited with {process.ExitCode}");
                switch (process.ExitCode)
                {
                    case 0: break;
                    case 3010: _note = "Visual Studio asked for a restart of Windows; restart if the build fails."; break;
                    case 1602: throw new InvalidOperationException("Visual Studio's installer was cancelled.");
                    default: throw new InvalidOperationException($"Visual Studio's installer failed with code {process.ExitCode}.");
                }
            }
        }

        private void InstallMinGit()
        {
            var archive = Path.Combine(Downloads, "MinGit.zip");
            Download(MinGitUrl, archive, "git", MinGitSha256);
            var target = Path.Combine(Tools, "mingit");
            DeleteDirectory(target);
            ZipFile.ExtractToDirectory(archive, target);
        }

        private string FetchSource(string git)
        {
            Set("source", NightlyStepState.Running);
            Directory.CreateDirectory(SourceRoot);
            CloneOrFetch(git, AppSource, AppRemote, AppRemoteFallback, Branch);
            RunTool(git, ["checkout", "--force", "--detach", "FETCH_HEAD"], AppSource, "source", null);
            RunTool(git, ["clean", "-ffdq", "-e", ".dotnet"], AppSource, "source", null);
            var commit = Capture(git, ["rev-parse", "HEAD"], AppSource);
            if (!IsCommit(commit)) throw new InvalidOperationException("The downloaded source has no commit.");

            // build.ps1 finds the pinned Avalonia commit in this clone itself.
            if (!Directory.Exists(Path.Combine(AvaloniaSource, ".git")))
                CloneOrFetch(git, AvaloniaSource, AvaloniaRemote, AvaloniaRemoteFallback, "main");
            Set("source", NightlyStepState.Done, null, commit[..7]);
            return commit;
        }

        private void CloneOrFetch(string git, string directory, string remote, string fallback, string branch)
        {
            if (!Directory.Exists(Path.Combine(directory, ".git")))
            {
                DeleteDirectory(directory);
                RunTool(git, ["init", "-q", directory], SourceRoot, "source", null);
                RunTool(git, ["remote", "add", "origin", remote], directory, "source", null);
            }
            var progress = GitProgress();
            try
            {
                RunTool(git, ["fetch", "--progress", "--filter=blob:none", "origin", branch], directory, "source", progress);
            }
            catch (InvalidOperationException) when (!token.IsCancellationRequested)
            {
                Write($"Fetching from {remote} failed; trying {fallback}.");
                RunTool(git, ["remote", "set-url", "origin", fallback], directory, "source", null);
                RunTool(git, ["fetch", "--progress", "--filter=blob:none", "origin", branch], directory, "source", progress);
            }
        }

        private void EnsureDotNet(string git)
        {
            Set("dotnet", NightlyStepState.Running);
            var version = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppSource, "global.json")))
                .RootElement.GetProperty("sdk").GetProperty("version").GetString();
            if (Directory.Exists(Path.Combine(AppSource, ".dotnet", "sdk", version ?? "-")))
            {
                Set("dotnet", NightlyStepState.Skipped);
                return;
            }
            RunScript(git, Path.Combine(AppSource, "eng", "Ensure-DotNet.ps1"), [], "dotnet", null);
            Set("dotnet", NightlyStepState.Done);
        }

        private void BuildApp(string git, string commit)
        {
            DeleteFile(ReadyFile);
            var output = Path.Combine(Root, "staging");
            var packages = 0;
            var phase = "avalonia";
            Set("avalonia", NightlyStepState.Running);
            var reused = false;
            RunScript(git, Path.Combine(AppSource, "build.ps1"), ["-Nightly", "-PublishDirectory", output], phase, line =>
            {
                var text = line.Trim();
                if (text.Contains("Using stamped Avalonia package set", StringComparison.Ordinal)) reused = true;
                else if (text.Contains("Successfully created package", StringComparison.Ordinal) && phase == "avalonia")
                    Set("avalonia", NightlyStepState.Running, Math.Min(0.99, (double)++packages / AvaloniaPackageCount), string.Empty);
                else if (text == "[nightly] app")
                {
                    Set("avalonia", reused ? NightlyStepState.Skipped : NightlyStepState.Done);
                    Set(phase = "app", NightlyStepState.Running);
                }
                else if (text == "[nightly] check")
                {
                    Set("app", NightlyStepState.Done);
                    Set(phase = "check", NightlyStepState.Running);
                }
                return phase;
            });
            foreach (var id in new[] { "avalonia", "app", "check" })
                if (StepState(id) is NightlyStepState.Pending or NightlyStepState.Running) Set(id, NightlyStepState.Done);

            if (!File.Exists(Path.Combine(output, "ClypDat.exe")))
                throw new InvalidOperationException("The build finished without ClypDat.exe.");
            DeleteDirectory(Staged);
            Directory.Move(output, Staged);
            File.WriteAllText(ReadyFile, JsonSerializer.Serialize(new { commit, built = DateTime.UtcNow }));
        }

        private void RunScript(string git, string script, string[] arguments, string step, Func<string, string>? onLine)
        {
            var list = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script };
            list.AddRange(arguments);
            var current = step;
            RunTool(PowerShell(), list, AppSource, step, line =>
            {
                if (onLine is not null) current = onLine(line);
                if (!string.IsNullOrWhiteSpace(line)) Detail(current, Shorten(line));
            }, git);
        }


        private Action<string> GitProgress() => line =>
        {
            var index = line.IndexOf('%');
            if (index <= 0) return;
            var start = index;
            while (start > 0 && char.IsDigit(line[start - 1])) start--;
            if (int.TryParse(line[start..index], out var percent))
                Set("source", NightlyStepState.Running, percent / 100.0, Shorten(line));
        };

        private string Capture(string file, IEnumerable<string> arguments, string directory)
        {
            var output = new StringBuilder();
            RunTool(file, arguments, directory, "source", line => output.AppendLine(line));
            return output.ToString().Trim().Split('\n').LastOrDefault()?.Trim() ?? string.Empty;
        }

        private void RunTool(string file, IEnumerable<string> arguments, string directory, string step, Action<string>? onLine, string? git = null)
        {
            token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = directory,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            PrepareEnvironment(start, git ?? (Path.GetFileName(file).Equals("git.exe", StringComparison.OrdinalIgnoreCase) ? file : FindGit()));
            Write($"> {Path.GetFileName(file)} {string.Join(' ', start.ArgumentList)}");

            using var process = new Process { StartInfo = start };
            var lastLines = new Queue<string>();
            void Line(string? text)
            {
                if (text is null) return;
                // Progress bars rewrite their line with \r.
                foreach (var part in text.Split('\r'))
                {
                    Write(part);
                    lock (lastLines)
                    {
                        lastLines.Enqueue(part);
                        if (lastLines.Count > 12) lastLines.Dequeue();
                    }
                    try { onLine?.Invoke(part); }
                    catch (Exception error) { Write($"(progress reader failed: {error.Message})"); }
                }
            }
            process.OutputDataReceived += (_, e) => Line(e.Data);
            process.ErrorDataReceived += (_, e) => Line(e.Data);
            if (!process.Start()) throw new InvalidOperationException($"{Path.GetFileName(file)} did not start.");
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch { }
            lock (Gate) _tool = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            while (!process.WaitForExit(250))
            {
                if (!token.IsCancellationRequested) continue;
                try { process.Kill(entireProcessTree: true); }
                catch { }
                token.ThrowIfCancellationRequested();
            }
            process.WaitForExit();
            lock (Gate) _tool = null;
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                string decisive;
                lock (lastLines)
                    decisive = lastLines.LastOrDefault(line => line.Contains("error", StringComparison.OrdinalIgnoreCase))
                               ?? lastLines.LastOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? string.Empty;
                throw new InvalidOperationException(
                    $"{Path.GetFileName(file)} failed ({process.ExitCode}). {decisive.Trim()}".Trim());
            }
        }

        private void Download(string url, string path, string step, string? sha256)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Write($"Downloading {url}");
            for (var attempt = 1; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("ClypDat-Nightly");
                    using var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).GetAwaiter().GetResult();
                    response.EnsureSuccessStatusCode();
                    var total = response.Content.Headers.ContentLength;
                    var partial = path + ".part";
                    using (var source = response.Content.ReadAsStream(token))
                    using (var target = File.Create(partial))
                    {
                        var buffer = new byte[81920];
                        long done = 0;
                        int read;
                        using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
                        while ((read = ReadWithStall(source, buffer, stall.Token)) > 0)
                        {
                            target.Write(buffer, 0, read);
                            done += read;
                            if (total > 0) Set(step, NightlyStepState.Running, (double)done / total.Value, $"Downloading · {done / 1_000_000} of {total.Value / 1_000_000} MB");
                        }
                    }
                    if (sha256 is not null)
                    {
                        using var stream = File.OpenRead(partial);
                        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                        if (actual != sha256)
                        {
                            stream.Dispose();
                            File.Delete(partial);
                            throw new InvalidOperationException($"The download of {Path.GetFileName(path)} did not match its published checksum.");
                        }
                    }
                    File.Move(partial, path, overwrite: true);
                    return;
                }
                catch (Exception error) when (attempt < 3 && error is HttpRequestException or IOException or TimeoutException && !token.IsCancellationRequested)
                {
                    Write($"Download attempt {attempt} failed: {error.Message}");
                    Thread.Sleep(TimeSpan.FromSeconds(3 * attempt));
                }
            }
        }

        private static int ReadWithStall(Stream source, byte[] buffer, CancellationToken token)
        {
            var read = source.ReadAsync(buffer, token).AsTask();
            if (!read.Wait(TimeSpan.FromSeconds(30), token)) throw new TimeoutException("The download stalled.");
            return read.Result;
        }


        private NightlyStepState StepState(string id) => _steps.First(step => step.Id == id).State;

        private void Set(string id, NightlyStepState state, double? progress = null, string? detail = null)
        {
            lock (_steps)
            {
                var index = _steps.FindIndex(step => step.Id == id);
                var previous = _steps[index];
                _steps[index] = previous with
                {
                    State = state,
                    Progress = state == NightlyStepState.Running ? progress : null,
                    Detail = detail ?? (state == previous.State ? previous.Detail : string.Empty)
                };
            }
            Publish(running: true);
        }

        private DateTime _lastDetail;

        private void Detail(string id, string detail)
        {
            if (DateTime.UtcNow - _lastDetail < TimeSpan.FromMilliseconds(250)) return;
            _lastDetail = DateTime.UtcNow;
            lock (_steps)
            {
                var index = _steps.FindIndex(step => step.Id == id);
                if (index < 0 || _steps[index].State != NightlyStepState.Running) return;
                _steps[index] = _steps[index] with { Detail = detail };
            }
            Publish(running: true);
        }

        private void FailRunning()
        {
            lock (_steps)
                for (var index = 0; index < _steps.Count; index++)
                    if (_steps[index].State == NightlyStepState.Running)
                        _steps[index] = _steps[index] with { State = NightlyStepState.Failed, Progress = null };
        }

        private void Publish(bool running)
        {
            NightlyReport report;
            lock (_steps)
                report = new NightlyReport(_steps.ToArray(), running, !running && _finished, _error, _note, _log, _commit);
            lock (Gate) _last = report;
            try { Changed?.Invoke(report); }
            catch (Exception error) { AppLog.Error("Nightly report handler failed", error); }
        }

        private void Write(string line)
        {
            try { _logWriter?.WriteLine(line); }
            catch { }
        }

        private static string Shorten(string line)
        {
            var text = line.Trim();
            return text.Length <= 140 ? text : text[..137] + "…";
        }
    }


    private static bool HasVisualStudio()
    {
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return false;
        try
        {
            var start = new ProcessStartInfo(vswhere, "-latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var process = Process.Start(start)!;
            var path = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            if (path.Length == 0 || !Directory.Exists(path)) return false;
            var cmake = File.Exists(Path.Combine(path, "Common7", "IDE", "CommonExtensions", "Microsoft", "CMake", "CMake", "bin", "cmake.exe"))
                        || OnPath("cmake.exe") is not null;
            var sdk = Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "Include", "10.0.26100.0"));
            return cmake && sdk;
        }
        catch { return false; }
    }

    private static string? FindGit()
    {
        var candidates = new[]
        {
            OnPath("git.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"),
            Path.Combine(Tools, "mingit", "cmd", "git.exe")
        };
        return candidates.FirstOrDefault(path => path is not null && File.Exists(path));
    }

    private static string PowerShell() =>
        OnPath("pwsh.exe") ?? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private static string? OnPath(string file)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), file);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    // Variables from ClypDat's own process or a developer's shell must not
    // steer the build to another SDK or MSBuild.
    private static void PrepareEnvironment(ProcessStartInfo start, string? git)
    {
        foreach (var key in start.Environment.Keys.ToArray())
            if (key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("NUGET_", StringComparison.OrdinalIgnoreCase) || key.Equals("MSBuildSDKsPath", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("MSBuildExtensionsPath", StringComparison.OrdinalIgnoreCase) || key.Equals("VSINSTALLDIR", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("CLYPDAT_", StringComparison.OrdinalIgnoreCase))
                start.Environment.Remove(key);
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (git is not null)
            start.Environment["PATH"] = Path.GetDirectoryName(git) + Path.PathSeparator + start.Environment["PATH"];
    }

    private static long FolderSize(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        try
        {
            return new DirectoryInfo(directory).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Sum(file => file.Length);
        }
        catch { return 0; }
    }

    private static long DeleteDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        var size = FolderSize(directory);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); }
            catch { }
        }
        Directory.Delete(directory, recursive: true);
        return size;
    }

    private static long DeleteFile(string path)
    {
        if (!File.Exists(path)) return 0;
        var size = new FileInfo(path).Length;
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
        return size;
    }
}

internal static class AuthenticodeCheck
{
    public static bool IsMicrosoftSigned(string path)
    {
        if (!Verify(path)) return false;
        try
        {
#pragma warning disable SYSLIB0057
            using var certificate = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return certificate.Subject.Contains("O=Microsoft Corporation", StringComparison.Ordinal);
        }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
    }

    private static bool Verify(string path)
    {
        var fileInfo = new WinTrustFileInfo { cbStruct = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = path };
        var filePointer = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)fileInfo.cbStruct);
        try
        {
            System.Runtime.InteropServices.Marshal.StructureToPtr(fileInfo, filePointer, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2, // WTD_UI_NONE
                fdwRevocationChecks = 0,
                dwUnionChoice = 1, // WTD_CHOICE_FILE
                pFile = filePointer,
                dwStateAction = 0,
                dwProvFlags = 0x80 // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.DestroyStructure<WinTrustFileInfo>(filePointer);
            System.Runtime.InteropServices.Marshal.FreeHGlobal(filePointer);
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [System.Runtime.InteropServices.DllImport("wintrust.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);
}

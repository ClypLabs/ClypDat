using System.Diagnostics;
using System.Security.Cryptography;

namespace ClypDat.App.Services;

internal static class CredentialStore
{
    public static byte[]? Load(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            var value = SecretAsync("lookup", path, null).GetAwaiter().GetResult();
            return string.IsNullOrWhiteSpace(value) ? null : Convert.FromBase64String(value.Trim());
        }
#if !CLYPDAT_LINUX
        return File.Exists(path) ? ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser) : null;
#else
        throw new PlatformNotSupportedException();
#endif
    }

    public static void Save(string path, byte[] bytes)
    {
        if (OperatingSystem.IsLinux())
        {
            SecretAsync("store", path, Convert.ToBase64String(bytes)).GetAwaiter().GetResult();
            return;
        }
#if !CLYPDAT_LINUX
        File.WriteAllBytes(path, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
#else
        throw new PlatformNotSupportedException();
#endif
    }

    public static void Delete(string path)
    {
        if (OperatingSystem.IsLinux()) SecretAsync("clear", path, null).GetAwaiter().GetResult();
        else File.Delete(path);
    }

    private static async Task<string> SecretAsync(string operation, string path, string? secret)
    {
        var start = new ProcessStartInfo("/usr/bin/secret-tool")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(operation);
        if (operation == "store") start.ArgumentList.Add("--label=ClypDat account credentials");
        start.ArgumentList.Add("application"); start.ArgumentList.Add("com.clyplabs.ClypDat");
        start.ArgumentList.Add("account"); start.ArgumentList.Add(Path.GetFullPath(path));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Secret Service client could not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            if (secret is not null) await process.StandardInput.WriteAsync(secret.AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await error.ConfigureAwait(false); // Do not include credential service output in logs.
            if (process.ExitCode != 0 && !(operation is "lookup" or "clear" && process.ExitCode == 1))
                throw new InvalidOperationException("Secret Service operation failed; credentials were not stored.");
            return await output.ConfigureAwait(false);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }
}

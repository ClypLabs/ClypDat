using System.IO.Pipes;
using System.Text.Json;

namespace ClypDat.Capture.Abstractions;

public static class CaptureWorkerProtocol
{
    public const int Version = 1;
    public const string PipePrefix = "ClypDat-CaptureWorker-";
    public const string MutexPrefix = "ClypDat-CaptureWorker-Mutex-";

    public static string UserId()
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName
                : Environment.UserName;
        }
        catch
        {
            return Environment.UserName;
        }
    }

    public static string UserSuffix() => UserId().Replace('-', '_').Replace('\\', '_');
    public static string PipeName => PipePrefix + UserSuffix();
    public static string MutexName => MutexPrefix + UserSuffix();
}

public sealed record CaptureWorkerEnvelope(
    int Version,
    string Type,
    Guid RequestId,
    JsonElement Payload);

public sealed record CaptureWorkerAck(bool Accepted, string Error = "");
public sealed record CaptureWorkerHandshake(int Version, string ClientId);
public sealed record CaptureWorkerAttachResponse(
    bool Recording,
    string ConfigIdentity,
    ReplayCaptureHealth Health,
    IReadOnlyList<CaptureWorkerSaveResult> UnacknowledgedSaves);
public sealed record CaptureWorkerSaveResult(string Path, string? Title, DateTime CompletedUtc, string? Error = null);

public static class CaptureWorkerPipe
{
    public static async Task WriteAsync(Stream stream, string type, Guid requestId, object? payload, CancellationToken cancellationToken)
    {
        var envelope = new
        {
            Version = CaptureWorkerProtocol.Version,
            Type = type,
            RequestId = requestId,
            Payload = payload
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope);
        var length = BitConverter.GetBytes(bytes.Length);
        await stream.WriteAsync(length, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<CaptureWorkerEnvelope?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[sizeof(int)];
        if (!await ReadExactlyAsync(stream, lengthBytes, cancellationToken)) return null;
        var length = BitConverter.ToInt32(lengthBytes, 0);
        if (length is <= 0 or > 16 * 1024 * 1024) throw new InvalidDataException("Invalid capture worker message length.");
        var bytes = new byte[length];
        if (!await ReadExactlyAsync(stream, bytes, cancellationToken)) return null;
        var envelope = JsonSerializer.Deserialize<CaptureWorkerEnvelope>(bytes);
        if (envelope is null) throw new InvalidDataException("Invalid capture worker message.");
        if (envelope.Version != CaptureWorkerProtocol.Version) throw new InvalidDataException($"Unsupported capture worker protocol version {envelope.Version}.");
        return envelope;
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    public static NamedPipeServerStream CreateServer()
        => new(CaptureWorkerProtocol.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    public static NamedPipeClientStream CreateClient()
        => new(".", CaptureWorkerProtocol.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
}

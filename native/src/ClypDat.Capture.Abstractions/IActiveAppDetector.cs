namespace ClypDat.Capture.Abstractions;

public interface IActiveAppDetector
{
    Task<ActiveAppInfo?> GetActiveAppAsync(CancellationToken cancellationToken = default);
}

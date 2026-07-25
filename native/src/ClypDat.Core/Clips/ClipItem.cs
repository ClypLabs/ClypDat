namespace ClypDat.Core.Clips;

public sealed record ClipItem(
    string Name,
    string Path,
    DateTimeOffset CreatedAt,
    TimeSpan Duration,
    long SizeBytes);

using Vortice.DXGI;

namespace ClypDat.App.Services;

public sealed record GraphicsAdapterOption(string Label, string Value)
{
    public bool IsAuto => string.IsNullOrEmpty(Value);
}

internal static class GraphicsAdapterService
{
    public static IReadOnlyList<GraphicsAdapterOption> Enumerate()
    {
        var options = new List<GraphicsAdapterOption>
        {
            new("Auto (recommended)", string.Empty)
        };

        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint index = 0; ; index++)
            {
                if (factory.EnumAdapters1(index, out var adapter).Failure) break;
                using (adapter)
                {
                    var description = adapter.Description1;
                    if ((description.Flags & AdapterFlags.Software) != 0) continue;

                    var name = description.Description.Trim();
                    if (string.IsNullOrWhiteSpace(name) || options.Any(option => string.Equals(option.Value, name, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    options.Add(new GraphicsAdapterOption(name, name));
                }
            }
        }
        catch (Exception error)
        {
            AppLog.Info($"GPU adapter enumeration unavailable; keeping Auto only ({error.Message}).");
        }

        return options;
    }

    public static IDXGIAdapter1? Find(string? requestedName)
    {
        if (string.IsNullOrWhiteSpace(requestedName)) return null;

        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint index = 0; ; index++)
            {
                if (factory.EnumAdapters1(index, out var adapter).Failure) break;
                var name = adapter.Description1.Description.Trim();
                if (string.Equals(name, requestedName.Trim(), StringComparison.OrdinalIgnoreCase)) return adapter;
                adapter.Dispose();
            }
        }
        catch (Exception error)
        {
            AppLog.Info($"Could not find requested GPU '{requestedName}' ({error.Message}).");
        }

        return null;
    }
}

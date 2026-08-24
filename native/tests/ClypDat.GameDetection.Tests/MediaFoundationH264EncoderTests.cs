using ClypDat.App.Services;
using Xunit;

namespace ClypDat.GameDetection.Tests;

public sealed class MediaFoundationH264EncoderTests
{
    [Fact]
    public void CodecApiConfiguration_PrefersLowLatencyCbrThroughput()
    {
        var settings = MediaFoundationH264CodecSettings.Create(15_000_000);

        Assert.Collection(settings,
            setting => { Assert.Equal(new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee"), setting.Property); Assert.Equal(true, setting.Value); },
            setting => { Assert.Equal(new Guid("98332df8-03cd-476b-89fa-3f9e442dec9f"), setting.Property); Assert.Equal(0u, setting.Value); },
            setting => { Assert.Equal(new Guid("1c0608e9-370c-4710-8a58-cb6181c42423"), setting.Property); Assert.Equal(0u, setting.Value); },
            setting => { Assert.Equal(new Guid("f7222374-2144-4815-b550-a37f8e12ee52"), setting.Property); Assert.Equal(15_000_000u, setting.Value); },
            setting => { Assert.Equal(new Guid("0db96574-b6a4-4c8b-8106-3773de0310cd"), setting.Property); Assert.Equal(15_000_000u, setting.Value); });
    }

    [Fact]
    public void AsyncPump_DrainsOutputWhileInputCreditWaitsForFrame()
    {
        var pump = new MediaFoundationAsyncPump();

        pump.OnNeedInput();

        Assert.Equal(1, pump.InputCredits);
        Assert.False(pump.TryAcceptInput(inputAvailable: false));
        pump.OnHaveOutput();
        Assert.True(pump.TryDrainOutput());
        Assert.Equal(1, pump.InputCredits);  // output remains eligible to drain
    }

    [Fact]
    public void AsyncPump_EachInputCreditAcceptsOnlyOneFrame()
    {
        var pump = new MediaFoundationAsyncPump();
        pump.OnNeedInput();

        Assert.True(pump.TryAcceptInput(inputAvailable: true));
        Assert.False(pump.TryAcceptInput(inputAvailable: true));

        pump.OnNeedInput();
        Assert.True(pump.TryAcceptInput(inputAvailable: true));
    }
}

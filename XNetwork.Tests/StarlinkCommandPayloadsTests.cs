using XNetwork.Services;

namespace XNetwork.Tests;

public class StarlinkCommandPayloadsTests
{
    [Fact]
    public void CreatePayload_BuildsRebootRequest()
    {
        var payload = StarlinkCommandPayloads.CreatePayload("reboot");

        Assert.Equal([0xca, 0x3e, 0x00], payload);
    }

    [Fact]
    public void CreatePayload_BuildsStowRequest()
    {
        var payload = StarlinkCommandPayloads.CreatePayload("stow");

        Assert.Equal([0x92, 0x7d, 0x00], payload);
    }

    [Fact]
    public void CreatePayload_BuildsUnstowRequest()
    {
        var payload = StarlinkCommandPayloads.CreatePayload("unstow");

        Assert.Equal([0x92, 0x7d, 0x02, 0x08, 0x01], payload);
    }
}

using XNetwork.Services;

namespace XNetwork.Tests;

public class AdapterNameResolverTests
{
    private static readonly Dictionary<string, string> Aliases =
        new(StringComparer.OrdinalIgnoreCase) { ["enx0"] = "Upstairs modem" };

    private static readonly Dictionary<string, string> IspNames =
        new(StringComparer.OrdinalIgnoreCase) { ["enx0"] = "Smart Communications", ["enx1"] = "Starlink" };

    private static readonly Dictionary<string, string> MetadataNames =
        new(StringComparer.OrdinalIgnoreCase) { ["enx0"] = "Smart", ["enx1"] = "Wired connection 2", ["enx2"] = "Globe" };

    [Fact]
    public void AliasWinsOverIspName()
    {
        Assert.Equal(
            "Upstairs modem",
            AdapterNameResolver.Resolve("enx0", Aliases, IspNames, MetadataNames, "Path 1"));
    }

    [Fact]
    public void IspNameWinsOverMetadataName()
    {
        Assert.Equal(
            "Starlink",
            AdapterNameResolver.Resolve("enx1", Aliases, IspNames, MetadataNames, "Path 2"));
    }

    [Fact]
    public void FallsBackToMetadataNameWhenNoIspName()
    {
        Assert.Equal(
            "Globe",
            AdapterNameResolver.Resolve("enx2", Aliases, IspNames, MetadataNames, "Path 3"));
    }

    [Fact]
    public void FallsBackToRuntimeNameWhenNothingElseKnown()
    {
        Assert.Equal(
            "Path 4",
            AdapterNameResolver.Resolve("enx9", Aliases, IspNames, MetadataNames, "Path 4"));
    }

    [Fact]
    public void FallsBackToInterfaceNameWhenRuntimeNameBlank()
    {
        Assert.Equal(
            "enx9",
            AdapterNameResolver.Resolve("enx9", Aliases, IspNames, MetadataNames, "   "));
    }

    [Fact]
    public void ReturnsEmptyStringWhenNothingIsKnownAtAll()
    {
        Assert.Equal(
            "",
            AdapterNameResolver.Resolve("", Aliases, IspNames, MetadataNames, null));
    }

    [Fact]
    public void IgnoresBlankDictionaryValues()
    {
        var blankIsp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["enx2"] = "  " };

        Assert.Equal(
            "Globe",
            AdapterNameResolver.Resolve("enx2", Aliases, blankIsp, MetadataNames, "Path 3"));
    }
}

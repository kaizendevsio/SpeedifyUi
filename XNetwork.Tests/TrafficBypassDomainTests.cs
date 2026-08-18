using Microsoft.Extensions.Logging.Abstractions;
using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

/// <summary>
/// Domain-based bypass exists because TikTok cannot be expressed as CIDRs: it resolves
/// into shared Akamai space (23.192.0.0/11 and friends) that fronts much of the internet,
/// and those addresses rotate. dnsmasq populates an nftables set from the real DNS answers
/// instead, and a rule matches that set.
/// </summary>
public class TrafficBypassDomainTests
{
    private static TrafficBypassRule DomainRule(params string[] domains) => new()
    {
        DisplayName = "TikTok",
        Domains = domains.ToList()
    };

    [Theory]
    [InlineData("tiktok.com")]
    [InlineData("www.tiktok.com")]
    [InlineData("p16-sign-va.tiktokcdn.com")]
    [InlineData("tiktokv.com")]
    [InlineData("xn--80ak6aa92e.com")]
    public void ValidDomainsAreAccepted(string domain)
    {
        Assert.True(TrafficBypassService.IsValidDomain(domain));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no_underscores.com")]
    [InlineData("-leadinghyphen.com")]
    [InlineData("trailinghyphen-.com")]
    [InlineData("double..dot.com")]
    [InlineData("http://tiktok.com")]
    [InlineData("tiktok.com/path")]
    [InlineData("192.168.1.1")]
    [InlineData("*")]
    public void InvalidDomainsAreRejected(string domain)
    {
        Assert.False(TrafficBypassService.IsValidDomain(domain));
    }

    [Fact]
    public void BareIpIsRejectedAsDomainSoItGoesInDestinations()
    {
        // A literal address belongs in Destinations; accepting it here would silently
        // create a DNS rule that never matches.
        Assert.False(TrafficBypassService.IsValidDomain("23.200.143.72"));
    }

    [Fact]
    public void DomainsAreNormalisedToLowercaseWithoutLeadingDots()
    {
        var rule = TrafficBypassService.NormalizeRule(
            DomainRule("TikTok.com", ".tiktokcdn.com", "tiktok.com"));

        Assert.Equal(["tiktok.com", "tiktokcdn.com"], rule.Domains);
    }

    [Fact]
    public void ADomainOnlyRuleIsValid()
    {
        var service = TestService();

        var result = service.ValidateRule(DomainRule("tiktok.com"));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void ARuleWithNoDestinationPortOrDomainIsRejected()
    {
        var service = TestService();

        var result = service.ValidateRule(new TrafficBypassRule { DisplayName = "Empty" });

        Assert.Contains(result.Errors, error => error.Contains("domain", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InvalidDomainProducesAValidationError()
    {
        var service = TestService();

        var result = service.ValidateRule(DomainRule("tiktok.com/path"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("tiktok.com/path"));
    }

    /// <summary>
    /// The apply helper reads the saved settings file directly, so the on-disk shape is a
    /// contract with it. The file is written with .NET's default PascalCase; the helper
    /// reads keys case-insensitively, which is what makes both work.
    /// </summary>
    [Fact]
    public void DomainsAreWrittenInTheShapeTheApplyHelperReads()
    {
        var settings = new TrafficBypassSettings
        {
            Rules = [TrafficBypassService.NormalizeRule(DomainRule("tiktok.com", "tiktokcdn.com"))]
        };

        var json = System.Text.Json.JsonSerializer.Serialize(
            settings,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var parsed = System.Text.Json.JsonSerializer.Deserialize<TrafficBypassSettings>(json);

        Assert.Equal(["tiktok.com", "tiktokcdn.com"], parsed!.Rules[0].Domains);
        // Domains must sit alongside its siblings rather than in a different casing.
        Assert.Contains("\"Domains\"", json);
        Assert.Contains("\"Rules\"", json);
        Assert.Contains("\"Destinations\"", json);
    }

    [Fact]
    public void TikTokPresetCoversTheDomainsThatDecideGeolocation()
    {
        var preset = TrafficBypassPresets.TikTok();

        // The SGD-vs-PHP currency comes from the API/webapp endpoints, not the video CDN,
        // so a preset that only covered tiktokcdn would not fix the reported problem.
        Assert.Contains("tiktok.com", preset.Domains);
        Assert.Contains("tiktokv.com", preset.Domains);
        Assert.Contains("tiktokcdn.com", preset.Domains);
        // TikTok Shop is what shows the currency and the deliver-to-region check.
        Assert.Contains("tiktokglobalshop.com", preset.Domains);
        Assert.Contains("tiktokshop.com", preset.Domains);
        // Observed live on the router: the app queries these constantly, and omitting them
        // left the iOS app still talking to Singapore endpoints.
        Assert.Contains("pangle.io", preset.Domains);
        Assert.Contains("tiktokpangle.us", preset.Domains);
        Assert.All(preset.Domains, domain => Assert.True(
            TrafficBypassService.IsValidDomain(domain),
            $"preset domain {domain} is not valid"));
        Assert.True(TrafficBypassService.NormalizeRule(preset).Domains.Count >= 4);
    }

    [Fact]
    public void BlockModeIsValidWithoutAnInterface()
    {
        var service = TestService();

        var result = service.ValidateRule(new TrafficBypassRule
        {
            DisplayName = "TikTok in-app DNS",
            Destinations = ["34.102.215.99"],
            EgressMode = TrafficBypassEgressModes.Block
        });

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal(
            TrafficBypassEgressModes.Block,
            TrafficBypassService.NormalizeRule(new TrafficBypassRule { EgressMode = "BLOCK" }).EgressMode);
    }

    private static TrafficBypassService TestService()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"bypass-domain-{Guid.NewGuid():N}.json");
        return new TrafficBypassService(
            new TrafficBypassSettings(),
            new TrafficBypassSettingsStore(NullLogger<TrafficBypassSettingsStore>.Instance, filePath),
            new InterfaceMetadataService(NullLogger<InterfaceMetadataService>.Instance),
            NullLogger<TrafficBypassService>.Instance);
    }
}

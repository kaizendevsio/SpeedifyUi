using XNetwork.Services;

namespace XNetwork.Tests;

public class AdapterIdentityResponseParserTests
{
    [Fact]
    public void ParsesIpWhoIsSuccessResponse()
    {
        const string json = """
        {
          "ip": "103.235.93.167",
          "success": true,
          "city": "Cebu City",
          "region": "Central Visayas",
          "country": "Philippines",
          "country_code": "PH",
          "connection": { "asn": 10139, "org": "Smart Communications", "isp": "Smart Communications Inc." }
        }
        """;

        var parsed = AdapterIdentityResponseParser.TryParse(json, out var result);

        Assert.True(parsed);
        Assert.Equal("103.235.93.167", result!.PublicIp);
        Assert.Equal("Smart Communications Inc.", result.Isp);
        Assert.Equal("Smart Communications", result.Organization);
        Assert.Equal("AS10139", result.AsLabel);
        Assert.Equal("Cebu City", result.City);
        Assert.Equal("Central Visayas", result.Region);
        Assert.Equal("Philippines", result.Country);
        Assert.Equal("PH", result.CountryCode);
        Assert.Equal("Smart Communications", result.DisplayName);
    }

    [Fact]
    public void RejectsIpWhoIsFailureResponse()
    {
        const string json = """{"ip":null,"success":false,"message":"Reserved range"}""";

        Assert.False(AdapterIdentityResponseParser.TryParse(json, out _));
    }

    [Fact]
    public void ParsesIpApiSuccessResponse()
    {
        const string json = """
        {
          "status": "success",
          "country": "Philippines",
          "countryCode": "PH",
          "regionName": "Cebu",
          "city": "Cebu City",
          "isp": "Starlink",
          "org": "SpaceX Starlink",
          "as": "AS14593 Space Exploration Technologies Corporation",
          "query": "146.75.1.1"
        }
        """;

        var parsed = AdapterIdentityResponseParser.TryParse(json, out var result);

        Assert.True(parsed);
        Assert.Equal("146.75.1.1", result!.PublicIp);
        Assert.Equal("Starlink", result.Isp);
        Assert.Equal("SpaceX Starlink", result.Organization);
        Assert.Equal("AS14593 Space Exploration Technologies Corporation", result.AsLabel);
        Assert.Equal("Cebu", result.Region);
        Assert.Equal("Starlink", result.DisplayName);
    }

    [Fact]
    public void RejectsIpApiFailureResponse()
    {
        const string json = """{"status":"fail","message":"private range","query":"192.168.1.1"}""";

        Assert.False(AdapterIdentityResponseParser.TryParse(json, out _));
    }

    [Fact]
    public void RejectsResponseWithoutUsefulFields()
    {
        Assert.False(AdapterIdentityResponseParser.TryParse("""{"foo":"bar"}""", out _));
    }

    [Fact]
    public void RejectsMalformedJson()
    {
        Assert.False(AdapterIdentityResponseParser.TryParse("not json", out _));
    }

    [Fact]
    public void FallsBackToOrganizationWhenIspMissing()
    {
        const string json = """{"ip":"1.2.3.4","success":true,"connection":{"org":"Converge ICT Solutions"}}""";

        Assert.True(AdapterIdentityResponseParser.TryParse(json, out var result));
        Assert.Equal("Converge ICT Solutions", result!.DisplayName);
    }

    [Theory]
    [InlineData("AS10139 Smart Communications Inc.", "Smart Communications")]
    [InlineData("Dito Telecommunity Corp.", "Dito Telecommunity")]
    [InlineData("  Globe   Telecom , Inc  ", "Globe Telecom")]
    [InlineData("Starlink", "Starlink")]
    [InlineData("AS14593", "AS14593")]
    [InlineData("LLC", "LLC")]
    [InlineData("The Constant Company, LLC", "The Constant Company")]
    [InlineData("Space Exploration Technologies Corporation", "Space Exploration Technologies")]
    [InlineData("Smart Broadband, Inc.", "Smart Broadband")]
    [InlineData("Globe Telecoms", "Globe Telecoms")]
    public void NormalizesProviderNames(string input, string expected)
    {
        Assert.Equal(expected, AdapterIdentityResponseParser.NormalizeProviderName(input));
    }

    [Fact]
    public void NormalizeReturnsNullForBlankInput()
    {
        Assert.Null(AdapterIdentityResponseParser.NormalizeProviderName("   "));
    }
}

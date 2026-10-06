using FastMoq;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using OssmSharp.Web.ServiceDefaults;
using OssmSharp.Web.ServiceDefaults.Models;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class AdminOptionsTests : MockerTestBase<AdminOptions>
{
    [Fact]
    public void LengthSettingsHaveDocumentedDefaults()
    {
        Assert.Equal(40, Component.RailLengthCm);
        Assert.Equal(LengthUnit.Centimetres, Component.LengthUnit);
    }

    [Theory]
    [InlineData("https://guest.example", true)]
    [InlineData("https://guest.example:8443/", true)]
    [InlineData("http://127.0.0.1:5001", true)]
    [InlineData("http://[::1]:5001", true)]
    [InlineData("http://localhost:5001", true)]
    [InlineData("http://guest.example", false)]
    [InlineData("ftp://guest.example", false)]
    [InlineData("https://guest.example/join", false)]
    [InlineData("https://guest.example/?invite=secret", false)]
    [InlineData("https://guest.example/#invite=secret", false)]
    [InlineData("https://user:password@guest.example", false)]
    [InlineData("https://*.example", false)]
    [InlineData("http://127.0.0.1.evil.example:5001", false)]
    [InlineData("http://localhost.evil.example:5001", false)]
    [InlineData("", false)]
    public void GuestHostnameRequiresHttpsExceptForLoopback(string origin, bool valid)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OSSM:Admin:ListenPort"] = "5002",
            ["OSSM:Admin:GuestHostname"] = origin,
        });
        builder.Services.AddAdminConfiguration();
        using var provider = builder.Services.BuildServiceProvider();

        if (valid)
            Assert.Equal(origin, provider.GetRequiredService<IOptions<AdminOptions>>().Value.GuestHostname);
        else
            Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AdminOptions>>().Value);
    }

    [Theory]
    [InlineData("1", 1d)]
    [InlineData("32.5", 32.5d)]
    [InlineData("500", 500d)]
    public void RailLengthIsConfiguredInCentimetres(string configured, double expected)
    {
        var options = Resolve(new() { ["OSSM:Admin:RailLengthCm"] = configured });

        Assert.Equal(expected, options.Value.RailLengthCm);
        Assert.Equal(LengthUnit.Centimetres, options.Value.LengthUnit);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("501")]
    public void RailLengthOutsideTheSupportedRangeIsRejected(string configured)
    {
        var options = Resolve(new() { ["OSSM:Admin:RailLengthCm"] = configured });

        Assert.Throws<OptionsValidationException>(() => options.Value);
    }

    [Theory]
    [InlineData("Inches", LengthUnit.Inches)]
    [InlineData("Centimetres", LengthUnit.Centimetres)]
    public void LengthUnitSelectsHowLengthsAreEntered(string configured, LengthUnit expected)
    {
        var options = Resolve(new() { ["OSSM:Admin:LengthUnit"] = configured });

        Assert.Equal(expected, options.Value.LengthUnit);
    }

    [Fact]
    public void UndefinedLengthUnitIsRejected()
    {
        var options = Resolve(new() { ["OSSM:Admin:LengthUnit"] = "42" });

        Assert.Throws<OptionsValidationException>(() => options.Value);
    }

    private static IOptions<AdminOptions> Resolve(Dictionary<string, string?> settings)
    {
        var builder = WebApplication.CreateBuilder();
        settings["OSSM:Admin:ListenPort"] = "5002";
        settings["OSSM:Admin:GuestHostname"] = "https://guest.example";
        builder.Configuration.AddInMemoryCollection(settings.Where(setting => setting.Value is not null));
        builder.Services.AddAdminConfiguration();
        return builder.Services.BuildServiceProvider().GetRequiredService<IOptions<AdminOptions>>();
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using OssmSharp.Web.ServiceDefaults;
using OssmSharp.Web.ServiceDefaults.Models;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class AdminOptionsTests
{
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
    [InlineData(null, 40d, LengthUnit.Centimetres)]
    [InlineData("1", 1d, LengthUnit.Centimetres)]
    [InlineData("32.5", 32.5d, LengthUnit.Centimetres)]
    [InlineData("500", 500d, LengthUnit.Centimetres)]
    public void RailLengthIsConfiguredInCentimetres(string? configured, double expected, LengthUnit unit)
    {
        var options = Resolve(new() { ["OSSM:Admin:RailLengthCm"] = configured });

        Assert.Equal(expected, options.Value.RailLengthCm);
        Assert.Equal(unit, options.Value.LengthUnit);
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

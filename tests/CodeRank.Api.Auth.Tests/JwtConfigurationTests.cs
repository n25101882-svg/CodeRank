using System.Security.Cryptography;
using CodeRank.Api.Authentication;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CodeRank.Api.Auth.Tests;

public sealed class JwtConfigurationTests
{
    private sealed class KeySource(string? value) : IJwtSigningKeySource { public string? Read()=>value; }
    [Theory]
    [InlineData("missingKey")][InlineData("badKey")][InlineData("shortKey")]
    [InlineData("issuer")][InlineData("audience")][InlineData("lifetime")]
    public void Invalid_configuration_is_rejected_without_echoing_values(string variant)
    {
        var values=new Dictionary<string,string?> { ["Jwt:Issuer"]="test",["Jwt:Audience"]="test-client",["Jwt:AccessTokenMinutes"]="15" };
        string? key=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        switch(variant) {
            case "missingKey":key=null;break;
            case "badKey":key="invalid-base64";break;
            case "shortKey":key=Convert.ToBase64String(RandomNumberGenerator.GetBytes(8));break;
            case "issuer":values.Remove("Jwt:Issuer");break;
            case "audience":values["Jwt:Audience"]=" ";break;
            case "lifetime":values["Jwt:AccessTokenMinutes"]="0";break;
        }
        var error=Assert.Throws<JwtConfigurationException>(()=>JwtSettings.Load(new ConfigurationBuilder().AddInMemoryCollection(values).Build(),new KeySource(key)));
        if(!string.IsNullOrEmpty(key))Assert.DoesNotContain(key,error.Message);
    }
    [Fact]
    public void Valid_configuration_uses_256_bit_or_stronger_key()
    {
        var values=new Dictionary<string,string?> { ["Jwt:Issuer"]="test",["Jwt:Audience"]="test-client",["Jwt:AccessTokenMinutes"]="15" };
        var settings=JwtSettings.Load(new ConfigurationBuilder().AddInMemoryCollection(values).Build(),new KeySource(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        Assert.Equal(256,settings.SigningKey.KeySize);Assert.Equal(TimeSpan.FromMinutes(15),settings.Lifetime);
    }
}

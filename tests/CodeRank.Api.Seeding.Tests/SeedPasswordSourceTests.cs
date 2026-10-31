using System.Security.Cryptography;
using CodeRank.Api.Data.Seeding;
using Xunit;

namespace CodeRank.Api.Seeding.Tests;

public sealed class SeedPasswordSourceTests
{
    [Fact]
    public void ReadsProcessEnvironmentInNonDevelopmentWithoutOtherConfigurationSources()
    {
        const string key = "Seeding__AdminPassword";
        var old = Environment.GetEnvironmentVariable(key);
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        try
        {
            Environment.SetEnvironmentVariable(key,password);
            using var source = new LocalSeedPasswordSource(new TestEnvironment("Testing"));
            Assert.True(password == source.GetPassword("Seeding:AdminPassword"));
        }
        finally { Environment.SetEnvironmentVariable(key,old); }
    }
}

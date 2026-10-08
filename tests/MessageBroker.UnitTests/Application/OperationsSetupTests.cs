using System.Text.RegularExpressions;
using MessageBroker.Application.Security;
using MessageBroker.Infrastructure;
using MessageBroker.Infrastructure.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MessageBroker.UnitTests.Application;

/// <summary>U12 — Data Protection keys survive a restart when a keys directory is configured.</summary>
public sealed class DataProtectionKeysTests : IDisposable
{
    private readonly string _keysDirectory = Path.Combine(Path.GetTempPath(), $"broker-keys-{Guid.NewGuid():N}");

    [Fact(DisplayName = "U12 A secret protected before a restart is readable after it")]
    public void Secret_survives_a_new_host()
    {
        string protectedSecret;
        using (var first = BuildProvider())
            protectedSecret = first.GetRequiredService<ISecretProtector>().Protect("whsec-value");

        using var second = BuildProvider();
        Assert.Equal("whsec-value", second.GetRequiredService<ISecretProtector>().Unprotect(protectedSecret));
        Assert.NotEmpty(Directory.GetFiles(_keysDirectory, "key-*.xml"));
    }

    private ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:BrokerDb"] = "Server=unused",
            ["Broker:DataProtection:KeysDirectory"] = _keysDirectory,
        }).Build();
        return new ServiceCollection().AddLogging().AddBrokerInfrastructure(configuration).BuildServiceProvider();
    }

    public void Dispose()
    {
        if (Directory.Exists(_keysDirectory))
            Directory.Delete(_keysDirectory, recursive: true);
    }
}

/// <summary>U13 — The DBA release script matches the DbUp deploy.</summary>
public class ReleaseScriptTests
{
    [Fact(DisplayName = "U13 Release script holds every script in deploy order, each migration guarded and journaled")]
    public void Script_follows_deploy_order_and_guards_migrations()
    {
        var script = ReleaseScript.Build();
        var scripts = SchemaDeployer.ReadAllScripts().Select(s => s.Name).ToList();

        var positions = scripts.Select(name => script.IndexOf($"-- ===== {name}", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Order(), positions);

        foreach (var migration in scripts.Where(SchemaDeployer.IsMigration))
        {
            Assert.Contains($"WHERE ScriptName = N'{migration}') SET NOEXEC ON;", script);
            Assert.Contains($"VALUES (N'{migration}', GETDATE());", script);
        }
        // Every NOEXEC ON is switched off again before the next script.
        Assert.Equal(Regex.Count(script, "SET NOEXEC ON;"), Regex.Count(script, "SET NOEXEC OFF;") - 1);
    }
}

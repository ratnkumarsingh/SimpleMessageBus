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

/// <summary>U14 — The full install script wraps the release script, and the committed copy is current.</summary>
public class FullScriptTests
{
    [Fact(DisplayName = "U14a Full script creates the database, holds the release script and reads every sqlcmd variable")]
    public void Full_script_wraps_the_release_script()
    {
        var script = ReleaseScript.BuildFull();

        Assert.Contains(ReleaseScript.Build().ReplaceLineEndings("\n"), script);
        Assert.Contains("CREATE DATABASE [$(DatabaseName)];", script);
        Assert.Contains("EXEC broker.usp_Application_EnsureBootstrapAdmin", script);
        Assert.Contains("EXEC broker.usp_AllowedHost_Seed", script);
        foreach (var variable in ReleaseScript.FullScriptVariables)
            Assert.Contains($"$({variable})", script);
        // :setvar would override the caller's -v values.
        Assert.DoesNotContain(script.Split('\n'),
            line => !line.StartsWith("--", StringComparison.Ordinal) && line.Contains(":setvar", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "U14b db/BrokerDb_Full.sql matches the generator (rerun db/build-release-script.ps1 -Full)")]
    public void Committed_full_script_is_current()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "db", "build-release-script.ps1")))
            dir = dir.Parent;
        Assert.True(dir is not null, "The repository root (with db/build-release-script.ps1) was not found above the test output.");

        var committed = File.ReadAllText(Path.Combine(dir.FullName, "db", "BrokerDb_Full.sql")).ReplaceLineEndings("\n");
        Assert.Equal(ReleaseScript.BuildFull(), committed);
    }
}

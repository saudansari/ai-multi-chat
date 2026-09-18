using System.Xml.Linq;

namespace SharedAgentChat.Tests;

public sealed class PackageGuardTests
{
    [Fact]
    public void Solution_does_not_reference_databases()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var csprojs = Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories);
        Assert.NotEmpty(csprojs);

        string[] banned =
        [
            "EntityFramework",
            "Microsoft.EntityFrameworkCore",
            "Npgsql",
            "Microsoft.Data.SqlClient",
            "Microsoft.Data.Sqlite",
            "StackExchange.Redis",
            "SQLite"
        ];

        foreach (var path in csprojs)
        {
            var xml = XDocument.Load(path);
            var packages = xml.Descendants("PackageReference")
                .Select(e => (string?)e.Attribute("Include") ?? "")
                .ToList();
            foreach (var name in packages)
            {
                Assert.DoesNotContain(banned, bannedName => name.Contains(bannedName, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public void Application_has_no_aspnet_or_http_packages()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "SharedAgentChat.Application", "SharedAgentChat.Application.csproj"));
        var xml = XDocument.Load(path);
        var packages = xml.Descendants("PackageReference")
            .Select(e => (string?)e.Attribute("Include") ?? "")
            .ToList();
        Assert.DoesNotContain(packages, p => p.Contains("AspNet", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(packages, p => p.Contains("Http", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(packages);
    }
}

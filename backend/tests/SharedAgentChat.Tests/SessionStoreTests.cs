using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SharedAgentChat.Domain;
using SharedAgentChat.Infrastructure;

namespace SharedAgentChat.Tests;

public sealed class SessionStoreTests
{
    [Fact]
    public void Memory_GetOrCreate_returns_same_instance_and_rejects_other_ids()
    {
        var repo = new InMemorySessionRepository();
        var first = repo.GetOrCreate(ChatSession.WellKnownId);
        var second = repo.GetOrCreate(ChatSession.WellKnownId);
        Assert.Same(first, second);
        Assert.Throws<SharedAgentChat.Application.ChatException>(() => repo.GetOrCreate("other"));
    }

    [Fact]
    public void File_mode_round_trips_messages_without_credentials()
    {
        var directory = Path.Combine(Path.GetTempPath(), "shared-agent-chat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "poc-session.json");

        try
        {
            var options = Options.Create(new SessionStoreOptions { Mode = "File", FilePath = path });
            var env = new TestHostEnvironment { ContentRootPath = directory };

            var first = new FileSessionRepository(
                new InMemorySessionRepository(),
                options,
                env,
                NullLogger<FileSessionRepository>.Instance);
            var session = first.GetOrCreate(ChatSession.WellKnownId);
            session.AddOrUpdateParticipant("c1", "Alice", "never-persist-me");
            session.AppendMessage("Alice", "hello");
            session.AppendMessage("Bob", "there");
            first.Save(session);

            var json = File.ReadAllText(path);
            Assert.Contains("hello", json, StringComparison.Ordinal);
            Assert.DoesNotContain("never-persist-me", json, StringComparison.Ordinal);
            Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);

            var second = new FileSessionRepository(
                new InMemorySessionRepository(),
                options,
                env,
                NullLogger<FileSessionRepository>.Instance);
            var reloaded = second.GetOrCreate(ChatSession.WellKnownId).GetTranscriptCopy();
            Assert.Equal(["hello", "there"], reloaded.Select(m => m.Text));
            Assert.DoesNotContain(reloaded, m => m.SenderDisplayName.Contains("credential", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void File_mode_fail_fast_on_corrupt_json()
    {
        var directory = Path.Combine(Path.GetTempPath(), "shared-agent-chat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "poc-session.json");
        File.WriteAllText(path, "{ not-json");

        try
        {
            var repo = new FileSessionRepository(
                new InMemorySessionRepository(),
                Options.Create(new SessionStoreOptions { FilePath = path }),
                new TestHostEnvironment { ContentRootPath = directory },
                NullLogger<FileSessionRepository>.Instance);

            var ex = Assert.Throws<InvalidOperationException>(() => repo.GetOrCreate(ChatSession.WellKnownId));
            Assert.Contains("Failed to read session file", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Solution_does_not_reference_a_database_stack()
    {
        var backendRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var csprojFiles = Directory.GetFiles(backendRoot, "*.csproj", SearchOption.AllDirectories);
        Assert.NotEmpty(csprojFiles);
        foreach (var file in csprojFiles)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("EntityFramework", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Sqlite", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Npgsql", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("StackExchange.Redis", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Microsoft.Data.SqlClient", text, StringComparison.OrdinalIgnoreCase);
        }
    }
}

file sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "tests";
    public string ContentRootPath { get; set; } = "";
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.NullFileProvider();
}

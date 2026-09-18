using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SharedAgentChat.Domain;
using SharedAgentChat.Infrastructure;

namespace SharedAgentChat.Tests;

public class FileSessionRepositoryTests
{
    [Fact]
    public void Restart_reloads_messages_and_file_has_no_credentials()
    {
        var temp = Path.Combine(Path.GetTempPath(), "shared-agent-chat-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var filePath = Path.Combine(temp, "poc-session.json");

        try
        {
            var options = Options.Create(new SessionStoreOptions { Mode = "File", FilePath = filePath });
            var env = new TestHostEnvironment(temp);

            var first = new FileSessionRepository(
                new InMemorySessionRepository(),
                options,
                env,
                NullLogger<FileSessionRepository>.Instance);

            var session = first.GetOrCreate(ChatSession.WellKnownId);
            session.AddOrUpdateParticipant("c1", "Alice", "crsr_should_not_be_written");
            session.AppendMessage("Alice", "one");
            session.AppendMessage("Bob", "two");
            first.Save(session);

            var json = File.ReadAllText(filePath);
            Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("crsr_should_not_be_written", json);

            var second = new FileSessionRepository(
                new InMemorySessionRepository(),
                options,
                env,
                NullLogger<FileSessionRepository>.Instance);
            var reloaded = second.GetOrCreate(ChatSession.WellKnownId).GetTranscriptCopy();
            Assert.Equal(["one", "two"], reloaded.Select(m => m.Text));
            Assert.All(reloaded, m => Assert.False(string.IsNullOrWhiteSpace(m.Id)));
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRootPath) => ContentRootPath = contentRootPath;

        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; }
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

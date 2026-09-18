using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedAgentChat.Application.Ports;
using SharedAgentChat.Domain;

namespace SharedAgentChat.Infrastructure;

public sealed class FileSessionRepository : ISessionRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ISessionRepository _inner;
    private readonly ILogger<FileSessionRepository> _logger;
    private readonly string _filePath;
    private readonly object _fileGate = new();
    private int _seeded;

    public FileSessionRepository(
        ISessionRepository inner,
        IOptions<SessionStoreOptions> options,
        IHostEnvironment environment,
        ILogger<FileSessionRepository> logger)
    {
        _inner = inner;
        _logger = logger;
        var configured = options.Value.FilePath;
        _filePath = Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configured));
    }

    public ChatSession GetOrCreate(string sessionId)
    {
        var session = _inner.GetOrCreate(sessionId);
        if (Volatile.Read(ref _seeded) == 0)
        {
            lock (_fileGate)
            {
                if (_seeded == 0)
                {
                    SeedFromFile(session);
                    _seeded = 1;
                }
            }
        }

        return session;
    }

    public void Save(ChatSession session)
    {
        lock (_fileGate)
        {
            WriteAtomically(session);
        }
    }

    private void SeedFromFile(ChatSession session)
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        var json = File.ReadAllText(_filePath);
        SessionFileDto snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<SessionFileDto>(json, JsonOptions)
                ?? throw new InvalidOperationException("Session file deserialized to null.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to read session file '{_filePath}'. Delete or fix the file and restart.",
                ex);
        }

        foreach (var message in snapshot.Messages)
        {
            session.AppendMessage(
                message.SenderDisplayName,
                message.Text,
                message.Timestamp,
                message.Id);
        }

        _logger.LogInformation(
            "Loaded {Count} messages for session {SessionId} from file.",
            snapshot.Messages.Count,
            session.Id);
    }

    private void WriteAtomically(ChatSession session)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var snapshot = new SessionFileDto
        {
            SessionId = session.Id,
            Messages = session.GetTranscriptCopy()
                .Select(m => new MessageFileDto
                {
                    Id = m.Id,
                    SenderDisplayName = m.SenderDisplayName,
                    Text = m.Text,
                    Timestamp = m.Timestamp
                })
                .ToList()
        };

        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        var tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);
    }

    private sealed class SessionFileDto
    {
        public string SessionId { get; set; } = ChatSession.WellKnownId;

        public List<MessageFileDto> Messages { get; set; } = [];
    }

    private sealed class MessageFileDto
    {
        public string Id { get; set; } = "";

        public string SenderDisplayName { get; set; } = "";

        public string Text { get; set; } = "";

        public DateTimeOffset Timestamp { get; set; }
    }
}

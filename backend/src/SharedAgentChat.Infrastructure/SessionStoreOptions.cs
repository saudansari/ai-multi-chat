namespace SharedAgentChat.Infrastructure;

public sealed class SessionStoreOptions
{
    public const string SectionName = "SessionStore";

    public string Mode { get; set; } = "File";

    public string FilePath { get; set; } = "data/poc-session.json";
}

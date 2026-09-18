namespace SharedAgentChat.Infrastructure;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public string Provider { get; set; } = "Stub";

    public int StubDelayMilliseconds { get; set; } = 800;

    public ComposerOptions Composer { get; set; } = new();
}

public sealed class ComposerOptions
{
    public string BaseUrl { get; set; } = "https://api.cursor.com";

    public string ModelId { get; set; } = "composer-2.5";

    public string Mode { get; set; } = "Fast";
}

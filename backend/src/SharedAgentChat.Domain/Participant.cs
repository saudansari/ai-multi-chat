namespace SharedAgentChat.Domain;

public sealed class Participant
{
    public Participant(string connectionId, string displayName, string? providerCredential = null)
    {
        ConnectionId = connectionId;
        DisplayName = displayName;
        ProviderCredential = providerCredential;
    }

    public string ConnectionId { get; set; }
    public string DisplayName { get; set; }
    public string? ProviderCredential { get; set; }
}

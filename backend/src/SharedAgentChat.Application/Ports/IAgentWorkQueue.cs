namespace SharedAgentChat.Application.Ports;

public interface IAgentWorkQueue
{
    void Enqueue(AgentWorkItem item);
}

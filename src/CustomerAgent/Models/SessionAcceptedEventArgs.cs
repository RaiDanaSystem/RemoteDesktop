namespace CustomerAgent.Models;

public sealed class SessionAcceptedEventArgs : EventArgs
{
    public Guid SessionId { get; init; }
    public string AgentName { get; init; } = string.Empty;
    public string AgentRole { get; init; } = string.Empty;
}

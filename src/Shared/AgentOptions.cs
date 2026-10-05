namespace RemoteSupport.Shared;

public sealed class AgentOptions
{
    public string ServerUrl { get; set; } = "http://localhost:5096";
    public string SupportUsername { get; set; } = "admin";
    public string SupportPassword { get; set; } = "Admin@12345";
}

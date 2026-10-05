using System.Collections.Concurrent;

namespace RemoteSupport.Server.Application.Services;

public interface ILoginAttemptTracker
{
    bool IsLocked(string username, string? ipAddress);
    void RecordFailure(string username, string? ipAddress);
    void RecordSuccess(string username, string? ipAddress);
}

public sealed class LoginAttemptTracker : ILoginAttemptTracker
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, AttemptState> _attempts = new();

    public bool IsLocked(string username, string? ipAddress)
    {
        if (!_attempts.TryGetValue(Key(username, ipAddress), out var state))
            return false;
        if (state.Failures < MaxFailures)
            return false;
        if (DateTime.UtcNow - state.LastFailureUtc < LockoutDuration)
            return true;
        _attempts.TryRemove(Key(username, ipAddress), out _);
        return false;
    }

    public void RecordFailure(string username, string? ipAddress)
    {
        _attempts.AddOrUpdate(
            Key(username, ipAddress),
            _ => new AttemptState { Failures = 1, LastFailureUtc = DateTime.UtcNow },
            (_, existing) =>
            {
                existing.Failures++;
                existing.LastFailureUtc = DateTime.UtcNow;
                return existing;
            });
    }

    public void RecordSuccess(string username, string? ipAddress)
        => _attempts.TryRemove(Key(username, ipAddress), out _);

    private static string Key(string username, string? ip)
        => $"{username.Trim().ToLowerInvariant()}|{ip ?? "-"}";

    private sealed class AttemptState
    {
        public int Failures;
        public DateTime LastFailureUtc;
    }
}

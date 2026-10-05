using RemoteSupport.Server.Domain.Entities;

namespace RemoteSupport.Server.Application.Interfaces;

public interface ISupportCodeService
{
    Task<SupportCode> GenerateCodeAsync(string deviceIdentifier, CancellationToken cancellationToken = default);
    Task<SupportCode?> ValidateCodeAsync(string code, CancellationToken cancellationToken = default);
    Task<SupportCode?> ValidateCodeForPollingAsync(string code, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SupportCode>> GetActiveCodesAsync(CancellationToken cancellationToken = default);
    Task<bool> DeviceHasValidCodeAsync(string deviceIdentifier, CancellationToken cancellationToken = default);
}

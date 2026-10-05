using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RemoteSupport.Server.Application.Interfaces;

namespace RemoteSupport.Server.Api.Controllers;

[ApiController]
[ApiVersion("1")]
[Route("api/v{version:apiVersion}/[controller]")]
[Authorize]
public class TransportController : ControllerBase
{
    private readonly ITransportService _transportService;
    private readonly ILogger<TransportController> _logger;

    public TransportController(ITransportService transportService, ILogger<TransportController> logger)
    {
        _transportService = transportService;
        _logger = logger;
    }

    [HttpPost("token")]
    public async Task<IActionResult> IssueToken([FromBody] TokenRequest request, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
            return Unauthorized(new { error = "Invalid user identity." });

        var result = await _transportService.IssueTransportTokenAsync(
            request.SessionId, userId.Value, cancellationToken);

        if (!result.IsSuccess)
            return BadRequest(new { error = result.ErrorMessage });

        return Ok(new
        {
            token = result.Token,
            expiresAtUtc = result.ExpiresAtUtc,
            sessionKey = result.SessionKey != null ? Convert.ToBase64String(result.SessionKey) : null
        });
    }

    [HttpPost("customer-token")]
    [AllowAnonymous]
    public async Task<IActionResult> IssueCustomerToken([FromBody] CustomerTokenRequest request, CancellationToken cancellationToken)
    {
        var result = await _transportService.IssueCustomerTransportTokenAsync(
            request.SessionId, request.DeviceIdentifier, cancellationToken);

        if (!result.IsSuccess)
            return BadRequest(new { error = result.ErrorMessage });

        return Ok(new
        {
            token = result.Token,
            expiresAtUtc = result.ExpiresAtUtc
        });
    }

    private Guid? GetCurrentUserId()
    {
        var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdStr, out var userId) ? userId : null;
    }

    public record TokenRequest(Guid SessionId);
    public record CustomerTokenRequest(Guid SessionId, string DeviceIdentifier);
}

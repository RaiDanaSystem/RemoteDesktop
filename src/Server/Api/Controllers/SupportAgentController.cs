using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RemoteSupport.Server.Application.Interfaces;

namespace RemoteSupport.Server.Api.Controllers;

[ApiController]
[ApiVersion("1")]
[Route("api/v{version:apiVersion}/[controller]")]
[Authorize(Roles = "SupportAgent,Admin")]
public class SupportAgentController : ControllerBase
{
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<SupportAgentController> _logger;

    public SupportAgentController(ISessionManager sessionManager, ILogger<SupportAgentController> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    [HttpPost("connect")]
    public async Task<IActionResult> Connect([FromBody] ConnectRequest request, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
            return Unauthorized(new { error = "Invalid user identity." });

        var result = await _sessionManager.InitiateConnectionAsync(
            userId.Value, request.SupportCode, cancellationToken);

        if (!result.IsSuccess)
        {
            _logger.LogWarning("Connection initiation failed: {Error}", result.ErrorMessage);
            return BadRequest(new { error = result.ErrorMessage });
        }

        return Ok(new
        {
            sessionId = result.SessionId,
            customerDeviceName = result.CustomerDeviceName,
            customerOperatingSystem = result.CustomerOperatingSystem,
            status = "Pending"
        });
    }

    [HttpGet("session/{sessionId}/status")]
    public async Task<IActionResult> GetSessionStatus(Guid sessionId, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        var result = await _sessionManager.GetSessionStatusAsync(sessionId, userId, null, cancellationToken);

        if (!result.SessionExists)
            return NotFound(new { error = "Session not found." });

        return Ok(new
        {
            sessionId = result.SessionId,
            status = result.Status,
            agentName = result.AgentName,
            agentDisplayName = result.AgentDisplayName,
            customerDeviceName = result.CustomerDeviceName,
            startedAtUtc = result.StartedAtUtc,
            endedAtUtc = result.EndedAtUtc,
            endReason = result.EndReason
        });
    }

    [HttpPost("session/{sessionId}/terminate")]
    public async Task<IActionResult> TerminateSession(Guid sessionId, [FromBody] SupportTerminateRequest? request, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        var result = await _sessionManager.TerminateSessionAsync(
            sessionId, userId, null, request?.Reason, cancellationToken);

        if (!result.IsSuccess)
            return BadRequest(new { error = result.ErrorMessage });

        return Ok(new { message = "Session terminated." });
    }

    private Guid? GetCurrentUserId()
    {
        var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdStr, out var userId) ? userId : null;
    }

    public record ConnectRequest(string SupportCode);
    public record SupportTerminateRequest(string? Reason);
}

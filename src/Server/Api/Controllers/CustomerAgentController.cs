using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RemoteSupport.Server.Application.DTOs;
using RemoteSupport.Server.Application.Interfaces;

namespace RemoteSupport.Server.Api.Controllers;

[ApiController]
[ApiVersion("1")]
[Route("api/v{version:apiVersion}/[controller]")]
public class CustomerAgentController : ControllerBase
{
    private readonly ICustomerAuthService _customerAuthService;
    private readonly ISupportCodeService _supportCodeService;
    private readonly ISessionManager _sessionManager;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly ILogger<CustomerAgentController> _logger;

    public CustomerAgentController(
        ICustomerAuthService customerAuthService,
        ISupportCodeService supportCodeService,
        ISessionManager sessionManager,
        IJwtTokenService jwtTokenService,
        ILogger<CustomerAgentController> logger)
    {
        _customerAuthService = customerAuthService;
        _supportCodeService = supportCodeService;
        _sessionManager = sessionManager;
        _jwtTokenService = jwtTokenService;
        _logger = logger;
    }

    [HttpPost("support-code")]
    [AllowAnonymous]
    public async Task<ActionResult<SupportCodeResponse>> GenerateSupportCode(
        [FromBody] SupportCodeRequest request, CancellationToken cancellationToken)
    {
        var code = await _supportCodeService.GenerateCodeAsync(
            request.DeviceIdentifier, cancellationToken);

        return Ok(new SupportCodeResponse(code.Code, code.ExpiresAtUtc));
    }

    [HttpPost("device-token")]
    [AllowAnonymous]
    public async Task<IActionResult> GetDeviceToken([FromBody] SupportCodeRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.DeviceIdentifier))
            return BadRequest(new { error = "Device identifier is required." });

        if (!await _supportCodeService.DeviceHasValidCodeAsync(request.DeviceIdentifier, cancellationToken))
            return Unauthorized(new { error = "Generate a support code for this device first." });

        var token = _jwtTokenService.GenerateDeviceToken(request.DeviceIdentifier);
        return Ok(new { token, expiresIn = TimeSpan.FromHours(8).TotalSeconds });
    }

    [HttpPost("auth")]
    [AllowAnonymous]
    public async Task<ActionResult<CustomerAuthResponse>> Authenticate(
        [FromBody] CustomerAuthRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _customerAuthService.AuthenticateAsync(
                request, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);

            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }

    [HttpGet("connection-request")]
    [AllowAnonymous]
    public async Task<IActionResult> CheckConnectionRequest(
        [FromQuery] string code, CancellationToken cancellationToken)
    {
        var supportCode = await _supportCodeService.ValidateCodeForPollingAsync(code, cancellationToken);
        if (supportCode is null)
        {
            return Ok(new { hasPendingRequest = false });
        }

        var result = await _sessionManager.GetPendingConnectionRequestAsync(
            supportCode.DeviceIdentifier, cancellationToken);

        return Ok(new
        {
            hasPendingRequest = result.HasPendingRequest,
            sessionId = result.SessionId,
            agentName = result.AgentDisplayName ?? result.AgentName,
            agentRole = result.AgentRole,
            requestedAtUtc = result.RequestedAtUtc
        });
    }

    [HttpPost("accept")]
    [Authorize(Roles = "Customer")]
    public async Task<IActionResult> AcceptConnection(
        [FromBody] AcceptRejectRequest request, CancellationToken cancellationToken)
    {
        var deviceIdentifier = GetDeviceIdentifierClaim();
        if (string.IsNullOrEmpty(deviceIdentifier))
            return Unauthorized(new { error = "Device token is required." });

        var result = await _sessionManager.AcceptConnectionAsync(
            request.SessionId, deviceIdentifier, cancellationToken);

        if (!result.IsSuccess)
            return BadRequest(new { error = result.ErrorMessage });

        return Ok(new { message = "Connection accepted." });
    }

    [HttpPost("reject")]
    [Authorize(Roles = "Customer")]
    public async Task<IActionResult> RejectConnection(
        [FromBody] AcceptRejectRequest request, CancellationToken cancellationToken)
    {
        var deviceIdentifier = GetDeviceIdentifierClaim();
        if (string.IsNullOrEmpty(deviceIdentifier))
            return Unauthorized(new { error = "Device token is required." });

        var result = await _sessionManager.RejectConnectionAsync(
            request.SessionId, deviceIdentifier, cancellationToken);

        if (!result.IsSuccess)
            return BadRequest(new { error = result.ErrorMessage });

        return Ok(new { message = "Connection rejected." });
    }

    [HttpGet("session/{sessionId}/status")]
    [Authorize(Roles = "Customer")]
    public async Task<IActionResult> GetSessionStatus(
        Guid sessionId, CancellationToken cancellationToken)
    {
        var deviceIdentifier = GetDeviceIdentifierClaim();
        var result = await _sessionManager.GetSessionStatusAsync(
            sessionId, null, deviceIdentifier, cancellationToken);

        if (!result.SessionExists)
            return NotFound(new { error = "Session not found." });

        return Ok(new
        {
            sessionId = result.SessionId,
            status = result.Status,
            agentName = result.AgentDisplayName ?? result.AgentName,
            customerDeviceName = result.CustomerDeviceName,
            startedAtUtc = result.StartedAtUtc,
            endedAtUtc = result.EndedAtUtc,
            endReason = result.EndReason
        });
    }

    [HttpPost("session/{sessionId}/terminate")]
    [Authorize(Roles = "Customer")]
    public async Task<IActionResult> TerminateSession(
        Guid sessionId, [FromBody] TerminateRequest? request, CancellationToken cancellationToken)
    {
        var deviceIdentifier = GetDeviceIdentifierClaim();
        if (string.IsNullOrEmpty(deviceIdentifier))
            return Unauthorized(new { error = "Device token is required." });

        var result = await _sessionManager.TerminateSessionAsync(
            sessionId, null, deviceIdentifier, request?.Reason, cancellationToken);

        if (!result.IsSuccess)
            return BadRequest(new { error = result.ErrorMessage });

        return Ok(new { message = "Session terminated." });
    }

    [HttpPost("disconnect")]
    [Authorize(Roles = "Customer")]
    public async Task<IActionResult> Disconnect(CancellationToken cancellationToken)
    {
        var deviceIdentifier = GetDeviceIdentifierClaim();
        if (!string.IsNullOrEmpty(deviceIdentifier))
        {
            await _sessionManager.TerminateOpenSessionsForDeviceAsync(
                deviceIdentifier, "Customer disconnected", cancellationToken);
        }

        var sessionIdStr = User.FindFirst("session_id")?.Value;
        if (Guid.TryParse(sessionIdStr, out _))
        {
            var token = Request.Headers.Authorization.ToString().Replace("Bearer ", "");
            await _customerAuthService.RevokeTokenAsync(token, cancellationToken);
        }

        return NoContent();
    }

    private string? GetDeviceIdentifierClaim()
        => User.FindFirst("device_identifier")?.Value
           ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    public record SupportCodeRequest(string DeviceIdentifier);
    public record SupportCodeResponse(string Code, DateTime ExpiresAtUtc);
    public record AcceptRejectRequest(Guid SessionId);
    public record TerminateRequest(string? Reason);
}

namespace RemoteSupport.Shared.Transport;

/// <summary>
/// Maps mouse coordinates between the SupportAgent display and the CustomerAgent remote screen.
/// Handles aspect-ratio-preserving letterboxing/pillarboxing and coordinate clamping.
/// </summary>
public sealed class CoordinateMapper
{
    private int _remoteWidth;
    private int _remoteHeight;
    private int _displayWidth;
    private int _displayHeight;
    private double _offsetX;
    private double _offsetY;
    private double _scale;

    public int RemoteWidth => _remoteWidth;
    public int RemoteHeight => _remoteHeight;
    public int DisplayWidth => _displayWidth;
    public int DisplayHeight => _displayHeight;

    public void UpdateDimensions(int remoteWidth, int remoteHeight, int displayWidth, int displayHeight)
    {
        if (displayWidth <= 0)
            displayWidth = remoteWidth;
        if (displayHeight <= 0)
            displayHeight = remoteHeight;

        _remoteWidth = remoteWidth;
        _remoteHeight = remoteHeight;
        _displayWidth = displayWidth;
        _displayHeight = displayHeight;

        if (remoteWidth <= 0 || remoteHeight <= 0 || displayWidth <= 0 || displayHeight <= 0)
        {
            _scale = 1.0;
            _offsetX = 0;
            _offsetY = 0;
            return;
        }

        var scaleX = (double)displayWidth / remoteWidth;
        var scaleY = (double)displayHeight / remoteHeight;
        _scale = Math.Min(scaleX, scaleY);

        var scaledWidth = remoteWidth * _scale;
        var scaledHeight = remoteHeight * _scale;
        _offsetX = (displayWidth - scaledWidth) / 2.0;
        _offsetY = (displayHeight - scaledHeight) / 2.0;
    }

    /// <summary>
    /// Maps a mouse position from the SupportAgent display coordinates to CustomerAgent remote screen coordinates.
    /// Accounts for letterboxing/pillarboxing and clamps to remote screen bounds.
    /// </summary>
    public (int X, int Y) MapToRemote(int displayX, int displayY)
    {
        if (_remoteWidth <= 0 || _remoteHeight <= 0)
            return (0, 0);

        var x = (displayX - _offsetX) / _scale;
        var y = (displayY - _offsetY) / _scale;

        return (
            X: Clamp((int)Math.Round(x), 0, _remoteWidth - 1),
            Y: Clamp((int)Math.Round(y), 0, _remoteHeight - 1)
        );
    }

    /// <summary>
    /// Maps a position from CustomerAgent remote screen coordinates to SupportAgent display coordinates.
    /// Used for rendering remote cursor overlay if needed.
    /// </summary>
    public (int X, int Y) ToDisplay(int remoteX, int remoteY)
    {
        var x = remoteX * _scale + _offsetX;
        var y = remoteY * _scale + _offsetY;

        return (
            X: Clamp((int)Math.Round(x), 0, Math.Max(0, _displayWidth - 1)),
            Y: Clamp((int)Math.Round(y), 0, Math.Max(0, _displayHeight - 1))
        );
    }

    /// <summary>
    /// Checks whether a display coordinate falls within the mapped remote image area
    /// (i.e., not in the letterbox/pillarbox bars).
    /// </summary>
    public bool IsWithinImage(int displayX, int displayY)
    {
        return displayX >= _offsetX
            && displayX <= _offsetX + _remoteWidth * _scale
            && displayY >= _offsetY
            && displayY <= _offsetY + _remoteHeight * _scale;
    }

    public bool IsConfigured => _remoteWidth > 0 && _remoteHeight > 0 && _displayWidth > 0 && _displayHeight > 0;

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
}

using RemoteSupport.Shared.Transport;

namespace RemoteSupport.Server.Tests.Transport;

public class CoordinateMapperTests
{
    [Fact]
    public void MapToRemote_TopLeft_ReturnsZeroZero()
    {
        var mapper = new CoordinateMapper();
        mapper.UpdateDimensions(1920, 1080, 800, 600);

        var (x, y) = mapper.MapToRemote(0, 0);

        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void MapToRemote_Center_ReturnsCenter()
    {
        var mapper = new CoordinateMapper();
        mapper.UpdateDimensions(1920, 1080, 800, 600);

        var (x, y) = mapper.MapToRemote(400, 300);

        Assert.Equal(960, x);
        Assert.Equal(540, y);
    }

    [Fact]
    public void MapToRemote_BottomRight_ReturnsMax()
    {
        var mapper = new CoordinateMapper();
        mapper.UpdateDimensions(1920, 1080, 800, 600);

        var (x, y) = mapper.MapToRemote(799, 599);

        Assert.True(x >= 1918 && x <= 1919);
        Assert.True(y >= 1078 && y <= 1079);
    }

    [Fact]
    public void MapToRemote_LetterboxedDisplay_MapsCorrectly()
    {
        var mapper = new CoordinateMapper();
        mapper.UpdateDimensions(1920, 1080, 1920, 1080);

        var (x, y) = mapper.MapToRemote(960, 540);

        Assert.Equal(960, x);
        Assert.Equal(540, y);
    }

    [Fact]
    public void MapToRemote_OutOfRangeCoordinates_ClampsToBounds()
    {
        var mapper = new CoordinateMapper();
        mapper.UpdateDimensions(1920, 1080, 800, 600);

        var (x, y) = mapper.MapToRemote(-100, -100);

        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void MapToRemote_ExceedsDisplay_ClampsToMax()
    {
        var mapper = new CoordinateMapper();
        mapper.UpdateDimensions(1920, 1080, 800, 600);

        var (x, y) = mapper.MapToRemote(10000, 10000);

        Assert.Equal(1919, x);
        Assert.Equal(1079, y);
    }

    [Fact]
    public void IsWithinImage_InsideImage_ReturnsTrue()
    {
        var mapper = new CoordinateMapper();
        mapper.UpdateDimensions(1920, 1080, 800, 600);

        Assert.True(mapper.IsWithinImage(400, 300));
    }

    [Fact]
    public void IsConfigured_AfterUpdate_ReturnsTrue()
    {
        var mapper = new CoordinateMapper();
        Assert.False(mapper.IsConfigured);

        mapper.UpdateDimensions(1920, 1080, 800, 600);
        Assert.True(mapper.IsConfigured);
    }

    [Fact]
    public void ToDisplay_RoundTrip_ReturnsOriginal()
    {
        var mapper = new CoordinateMapper();
        mapper.UpdateDimensions(1920, 1080, 800, 600);

        var (displayX, displayY) = mapper.ToDisplay(960, 540);
        var (remoteX, remoteY) = mapper.MapToRemote(displayX, displayY);

        Assert.Equal(960, remoteX);
        Assert.Equal(540, remoteY);
    }
}

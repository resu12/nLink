using NLink.Core.RemoteControl;

namespace NLink.SmokeTests;

[Trait("Area", "RemoteControl")]
public sealed class RemoteControlCoordinateMapperTests
{
    public static IEnumerable<object[]> UniformMappingCases()
    {
        yield return new object[]
        {
            "exact_fit_no_bars",
            960d,
            540d,
            1920d,
            1080d,
            1920d,
            1080d,
            0.5d,
            0.5d,
        };

        yield return new object[]
        {
            "letterbox_top_bottom_bars",
            960d,
            600d,
            1920d,
            1200d,
            1920d,
            1080d,
            0.5d,
            0.5d,
        };

        yield return new object[]
        {
            "pillarbox_left_right_bars",
            1200d,
            540d,
            2400d,
            1080d,
            1920d,
            1080d,
            0.5d,
            0.5d,
        };

        yield return new object[]
        {
            "non_integer_scaling",
            500d,
            350d,
            1000d,
            700d,
            1366d,
            768d,
            0.5d,
            0.5d,
        };

        yield return new object[]
        {
            "very_small_viewer",
            0.5d,
            0.5d,
            1d,
            1d,
            1920d,
            1080d,
            0.5d,
            0.5d,
        };

        yield return new object[]
        {
            "out_of_bounds_pointer_positions_are_clamped",
            -100d,
            5000d,
            1920d,
            1080d,
            1920d,
            1080d,
            0d,
            1d,
        };
    }

    [Theory]
    [MemberData(nameof(UniformMappingCases))]
    public void TryMapPointerToNormalized_UniformMode_ReturnsExpectedNormalizedCoordinates(
        string _,
        double pointerX,
        double pointerY,
        double viewerWidth,
        double viewerHeight,
        double frameWidth,
        double frameHeight,
        double expectedNx,
        double expectedNy)
    {
        var mapped = RemoteControlCoordinateMapper.TryMapPointerToNormalized(
            pointerX,
            pointerY,
            viewerWidth,
            viewerHeight,
            frameWidth,
            frameHeight,
            out var nx,
            out var ny,
            RemoteControlViewerStretchMode.Uniform);

        Assert.True(mapped);
        AssertClose(expectedNx, nx);
        AssertClose(expectedNy, ny);
    }

    [Fact]
    public void TryMapPointerToNormalized_InvalidDimensions_ReturnsFalse()
    {
        var mapped = RemoteControlCoordinateMapper.TryMapPointerToNormalized(
            pointerX: 10d,
            pointerY: 10d,
            viewerWidth: 0d,
            viewerHeight: 100d,
            frameWidth: 1920d,
            frameHeight: 1080d,
            out var nx,
            out var ny,
            RemoteControlViewerStretchMode.Uniform);

        Assert.False(mapped);
        Assert.Equal(0d, nx);
        Assert.Equal(0d, ny);
    }

    [Fact]
    public void TryMapPointerToNormalized_StretchMode_MapsAgainstViewerBounds()
    {
        var mapped = RemoteControlCoordinateMapper.TryMapPointerToNormalized(
            pointerX: 300d,
            pointerY: 150d,
            viewerWidth: 600d,
            viewerHeight: 300d,
            frameWidth: 1920d,
            frameHeight: 1080d,
            out var nx,
            out var ny,
            RemoteControlViewerStretchMode.Stretch);

        Assert.True(mapped);
        AssertClose(0.5d, nx);
        AssertClose(0.5d, ny);
    }

    private static void AssertClose(double expected, double actual)
    {
        const double epsilon = 0.000001d;
        Assert.InRange(actual, expected - epsilon, expected + epsilon);
    }
}

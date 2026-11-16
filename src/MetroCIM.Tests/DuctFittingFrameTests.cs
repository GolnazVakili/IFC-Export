using System.Numerics;
using MetroCIM.Services;
using Xunit;

namespace MetroCIM.Tests;

public sealed class DuctFittingFrameTests
{
    [Fact]
    public void ARiserKeepsAFullCrossSection()
    {
        IReadOnlyList<SweepFrame> frames = DuctFittingFrames.Sweep(
            [new Vector3(2, 0, 0), new Vector3(0, 0, 0), new Vector3(0, 0, 2)],
            Vector3.UnitY);

        Assert.Equal(2, frames.Count);
        SweepFrame riser = frames[1];
        Assert.True(riser.Up.Length() > 0.99f);
        Assert.True(Math.Abs(Vector3.Dot(riser.Up, riser.Axis)) < 0.05f);
        Assert.True(Math.Abs(riser.Right.Y) > 0.9f);
        Assert.True(Math.Abs(frames[0].Up.Z) > 0.9f);
    }

    [Fact]
    public void AnLBendTurnsTheThinSideWithTheTray()
    {
        IReadOnlyList<SweepFrame> frames = DuctFittingFrames.Sweep(
            [new Vector3(2, 0, 0), new Vector3(0, 0, 0), new Vector3(0, 0, 2)],
            Vector3.UnitY);

        SweepFrame riser = frames[1];
        Assert.True(Math.Abs(riser.Up.Z) < 0.2f);
        Assert.True(Math.Abs(Vector3.Dot(riser.Up, riser.Axis)) < 0.05f);
    }
}

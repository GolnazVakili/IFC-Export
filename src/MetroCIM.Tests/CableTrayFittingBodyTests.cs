using System.Numerics;
using MetroCIM.Services;
using Xunit;

namespace MetroCIM.Tests;

public sealed class CableTrayFittingBodyTests
{
    [Fact]
    public void AHorizontalRunStaysOpenUpward()
    {
        Vector3 opening = CableTrayFittingBody.AlignOpening(Vector3.UnitZ, Vector3.UnitX);

        Assert.True(opening.Z > 0.9f);
    }

    [Fact]
    public void AVerticalRunDoesNotStandUpAsAPlate()
    {
        Vector3 opening = CableTrayFittingBody.AlignOpening(Vector3.UnitZ, Vector3.UnitZ);

        Assert.True(Math.Abs(opening.Z) < 0.1f);
        Assert.True(Math.Abs(Vector3.Dot(opening, Vector3.UnitZ)) < 0.1f);
    }

    [Fact]
    public void ElbowBendIsTheIntersectionOfTheTwoLegs()
    {
        Vector3 bend = CableTrayFittingBody.BendOf(
            new Vector3(1, 0, 0),
            new Vector3(-1, 0, 0),
            new Vector3(0, 1, 0),
            new Vector3(0, -1, 0));

        Assert.True(bend.Length() < 0.001f);
    }

    [Fact]
    public void ABendFollowsACurveInsteadOfTheStraightChord()
    {
        IReadOnlyList<Vector3> path = CableTrayFittingBody.Centerline(
            new Vector3(1, 0, 0),
            new Vector3(-1, 0, 0),
            new Vector3(0, 1, 0),
            new Vector3(0, 1, 0));

        Assert.True(path.Count > 2);
        Vector3 middle = path[path.Count / 2];
        Vector3 chord = new Vector3(0.5f, 0.5f, 0);
        Assert.True(Vector3.Distance(middle, chord) > 0.05f);
    }

    [Fact]
    public void AFittingReachesIntoBothRuns()
    {
        IReadOnlyList<Vector3> path = CableTrayFittingBody.ExtendIntoRuns(
            [new Vector3(1, 0, 0), new Vector3(0, 1, 0)],
            new Vector3(1, 0, 0),
            new Vector3(0, 1, 0),
            0.1f);

        Assert.Equal(4, path.Count);
        Assert.True(path[0].X > 1.05f);
        Assert.True(path[^1].Y > 1.05f);
        Assert.Contains(path, point => Vector3.Distance(point, new Vector3(1, 0, 0)) < 0.001f);
        Assert.Contains(path, point => Vector3.Distance(point, new Vector3(0, 1, 0)) < 0.001f);
    }

    [Fact]
    public void AStraightFittingStaysASingleSegment()
    {
        IReadOnlyList<Vector3> path = CableTrayFittingBody.Centerline(
            new Vector3(0, 0, 0),
            Vector3.UnitX,
            new Vector3(2, 0, 0),
            Vector3.UnitX);

        Assert.Equal(2, path.Count);
    }

    [Fact]
    public void ElbowHasTheTrayCrossSection()
    {
        var bend = new Vector3(0, 0, 0);
        var legs = new FittingLeg[]
        {
            new(new Vector3(1, 0, 0), Vector3.UnitZ, 0.3f, 0.1f),
            new(new Vector3(0, 0, 1), Vector3.UnitX, 0.3f, 0.1f)
        };

        var mesh = CableTrayFittingBody.Build(legs, bend);

        Assert.False(mesh.IsEmpty);
        Assert.False(mesh.IsPaperThin);
        Assert.Contains(mesh.Positions, point => point.X > 0.9f);
        Assert.Contains(mesh.Positions, point => point.Z > 0.9f);
    }
}

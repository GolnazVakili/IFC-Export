using System.Numerics;
using MetroCIM.Services;
using Xunit;

namespace MetroCIM.Tests;

public sealed class DuctFittingPathTests
{
    [Fact]
    public void AReversedConnectorStillTurnsThroughTheBend()
    {
        var center = new Vector3(1, 0, 1);
        Vector3 horizontal = DuctFittingPath.Outward(new Vector3(2, 0, 0), -Vector3.UnitX, center);
        Vector3 vertical = DuctFittingPath.Outward(new Vector3(0, 0, 2), Vector3.UnitZ, center);
        OrientedEnd start = End(new Vector3(2, 0, 0), horizontal, Vector3.UnitY, 1.2f, 0.6f);
        OrientedEnd finish = End(new Vector3(0, 0, 2), vertical, Vector3.UnitY, 1.2f, 0.6f);

        IReadOnlyList<Vector3> path = DuctFittingPath.Elbow(start, finish, 0.2f, 0.2f);

        Assert.True(path[0].X > 2f);
        Assert.True(path[^1].Z > 2f);
        Vector3 middle = path[path.Count / 2];
        Assert.InRange(middle.X, 0f, 2f);
        Assert.InRange(middle.Z, 0f, 2f);
        Assert.True(Math.Abs(middle.Y) < 0.05f);
    }

    [Fact]
    public void ATeeMeetsAtTheConnectorIntersection()
    {
        OrientedEnd[] ends =
        [
            End(new Vector3(2, 0, 0), Vector3.UnitX, Vector3.UnitY, 1f, 0.5f),
            End(new Vector3(-2, 0, 0), -Vector3.UnitX, Vector3.UnitY, 1f, 0.5f),
            End(new Vector3(0, 2, 0), Vector3.UnitY, Vector3.UnitX, 1f, 0.5f)
        ];

        Vector3 junction = DuctFittingPath.Junction(ends);

        Assert.True(junction.Length() < 0.2f);
        Assert.True(DuctFittingPath.Meets(ends[2], junction, DuctFittingPath.Span(ends)));
        Assert.False(DuctFittingPath.Meets(ends[0], new Vector3(0, 0, 8), DuctFittingPath.Span(ends)));
    }

    [Fact]
    public void AReversedTangentDoesNotBalloonOutsideTheCorner()
    {
        var start = new OrientedEnd(new Vector3(2, 0, 0), -Vector3.UnitX, Vector3.UnitX, Vector3.UnitY, 1f, 0.5f, false);
        var end = new OrientedEnd(new Vector3(0, 0, 2), -Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitY, 1f, 0.5f, false);

        IReadOnlyList<Vector3> path = DuctFittingPath.Elbow(start, end, 0.1f, 0.1f);

        Assert.All(path, point =>
        {
            Assert.InRange(point.X, -0.3f, 2.3f);
            Assert.InRange(point.Z, -0.3f, 2.3f);
            Assert.True(Math.Abs(point.Y) < 0.05f);
        });
        Vector3 middle = path[path.Count / 2];
        Assert.True(middle.X < 1.6f && middle.Z < 1.6f);
    }

    [Fact]
    public void ReachStopsAtTheConnectedRun()
    {
        float reach = DuctFittingPath.Reach(1.2f, 0.6f, 0.5f);

        Assert.InRange(reach, 0.5f, 0.8f);
        Assert.True(DuctFittingPath.Reach(1f, 0.5f, 20f) < 2f);
    }

    private static OrientedEnd End(Vector3 origin, Vector3 outward, Vector3 width, float widthSize, float height)
    {
        Vector3 away = Vector3.Normalize(outward);
        return new OrientedEnd(
            origin,
            away,
            -away,
            DuctFittingPath.ProfileRight(width, away),
            widthSize,
            height,
            false);
    }
}

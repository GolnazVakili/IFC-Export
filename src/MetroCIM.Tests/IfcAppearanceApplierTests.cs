using MetroCIM.Models;
using MetroCIM.Services;
using System.Numerics;
using Xunit;

namespace MetroCIM.Tests;

public sealed class IfcAppearanceApplierTests
{
    [Fact]
    public void MaterialNameIsStableForASystemTypeColor()
    {
        ResolvedAppearance chilled = ResolvedAppearance.FromRgb(0, 176, 240);
        Assert.Equal("MetroCIM 000-176-240-255", IfcAppearanceApplier.MaterialName(chilled));
    }

    [Fact]
    public void MaterialNameIncludesTransparency()
    {
        ResolvedAppearance glass = ResolvedAppearance.FromRgb(180, 180, 180, 40);
        Assert.Equal("MetroCIM 180-180-180-153", IfcAppearanceApplier.MaterialName(glass));
    }

    [Fact]
    public void EmptyGuidEncodesToTwentyTwoZeros()
    {
        Assert.Equal("0000000000000000000000", IfcGuid.From(Guid.Empty));
    }

    [Fact]
    public void ALongFlatMeshIsPaperThin()
    {
        var mesh = new TriangleMesh();
        mesh.AddTriangle(new Vector3(0, 0, 0), new Vector3(2, 0, 0), new Vector3(2, 0.001f, 0));
        Assert.True(mesh.IsPaperThin);
    }

    [Fact]
    public void ATraySizedMeshIsNotPaperThin()
    {
        var mesh = new TriangleMesh();
        mesh.AddTriangle(new Vector3(0, 0, 0), new Vector3(2, 0, 0), new Vector3(2, 0.1f, 0));
        mesh.AddTriangle(new Vector3(0, 0, 0), new Vector3(2, 0, 0), new Vector3(2, 0, 0.1f));
        Assert.False(mesh.IsPaperThin);
    }
}

using MetroCIM.Services;
using Xunit;

namespace MetroCIM.Tests;

public sealed class ShortCurveTests
{
    [Theory]
    [InlineData(0.01, 0.0026, true)]
    [InlineData(0.0026, 0.0026, false)]
    [InlineData(0.001, 0.0026, false)]
    public void IsLongEnoughUsesTheTolerance(double length, double tolerance, bool expected)
    {
        Assert.Equal(expected, ShortCurve.IsLongEnough(length, tolerance));
    }

    [Fact]
    public void ExportFailureMatchesTheRevitMessage()
    {
        const string message =
            "Curve length is too small for Revit's tolerance (as identified by Application.ShortCurveTolerance).\r\nParameter name: endpoints";

        Assert.True(ShortCurve.IsExportFailure(message));
        Assert.False(ShortCurve.IsExportFailure("Revit did not write the IFC file."));
        Assert.False(ShortCurve.IsExportFailure(null));
    }

    [Fact]
    public void HighDetailIsTriedBeforeTheLowerFallbacks()
    {
        Assert.Equal(ShortCurve.PreferredTessellation, ShortCurve.TessellationAttempts[0]);
        Assert.Equal(0.8, ShortCurve.PreferredTessellation);
        Assert.Equal([0.8, 0.5, 0.25], ShortCurve.TessellationAttempts);
    }

    [Fact]
    public void LowerDetailNoteNamesTheTessellationThatWasUsed()
    {
        string note = ShortCurve.LowerDetailNote(0.5);

        Assert.Contains("0.50", note, StringComparison.Ordinal);
        Assert.Contains("tolerance", note, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0.8, "0.8")]
    [InlineData(0.5, "0.5")]
    [InlineData(0.25, "0.25")]
    public void TessellationOptionUsesADotDecimal(double tessellation, string expected)
    {
        Assert.Equal(expected, ShortCurve.FormatTessellation(tessellation));
    }
}

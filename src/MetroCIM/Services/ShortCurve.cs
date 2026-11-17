using System.Globalization;

namespace MetroCIM.Services;

public static class ShortCurve
{
    public const double PreferredTessellation = 0.8;

    public static readonly double[] TessellationAttempts = [0.8, 0.5, 0.25];

    public static bool IsLongEnough(double length, double tolerance) =>
        length > tolerance;

    public static bool IsExportFailure(string? error) =>
        !string.IsNullOrWhiteSpace(error) &&
        error.Contains("ShortCurveTolerance", StringComparison.OrdinalIgnoreCase);

    public static string LowerDetailNote(double tessellation) =>
        "High detail was skipped because a curve is shorter than Revit's tolerance (about 1/32 inch). " +
        "This file was written at tessellation " +
        tessellation.ToString("0.00", CultureInfo.InvariantCulture) + ".";

    public static string FormatTessellation(double tessellation) =>
        tessellation.ToString("0.0########", CultureInfo.InvariantCulture);
}

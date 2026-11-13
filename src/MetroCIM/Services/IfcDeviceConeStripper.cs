using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace MetroCIM.Services;

/// <summary>
/// Removes the pyramid/cone light-source solid that Revit leaves on devices.
/// Pipe, duct, and cable fittings are left alone.
/// </summary>
public static partial class IfcDeviceConeStripper
{
    public static int Apply(string path)
    {
        if (!File.Exists(path) ||
            Path.GetExtension(path).Equals(".ifcxml", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (Path.GetExtension(path).Equals(".ifczip", StringComparison.OrdinalIgnoreCase))
            return ApplyZip(path);

        return PatchFile(path);
    }

    public static string Strip(string ifcText)
    {
        var entities = new Dictionary<int, Entity>();
        var lines = ifcText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        for (int i = 0; i < lines.Count; i++)
        {
            Match match = EntityLine().Match(lines[i].Trim());
            if (!match.Success)
                continue;

            int id = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            entities[id] = new Entity(id, match.Groups[2].Value, match.Groups[3].Value, i);
        }

        var protectedFacesets = new HashSet<int>();
        foreach (Entity entity in entities.Values)
        {
            if (!IsProtectedProduct(entity.Type))
                continue;

            CollectFacesets(entities, entity.Id, [], protectedFacesets);
        }

        bool changed = false;
        foreach (Entity entity in entities.Values)
        {
            if (entity.Type != "IFCTRIANGULATEDFACESET" || protectedFacesets.Contains(entity.Id))
                continue;

            if (!TryStripCones(entities, entity, out string rewritten))
                continue;

            lines[entity.LineIndex] = rewritten;
            changed = true;
        }

        if (!changed)
            return ifcText;

        return string.Join("\n", lines) + (ifcText.EndsWith('\n') || ifcText.EndsWith("\r\n") ? "\n" : string.Empty);
    }

    private static bool TryStripCones(Dictionary<int, Entity> entities, Entity faceset, out string rewritten)
    {
        rewritten = string.Empty;
        IReadOnlyList<string> args = IfcColorPatcher.SplitArgs(faceset.Inner);
        int indexArg = -1;
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith("((", StringComparison.Ordinal))
            {
                indexArg = i;
                break;
            }
        }

        if (indexArg < 0 || !TryParseTriangles(args[indexArg], out List<(int A, int B, int C)> triangles))
            return false;

        if (!TryLoadPoints(entities, args, out List<(double X, double Y, double Z)> points))
            return false;

        List<List<int>> components = Components(triangles);
        var drop = new HashSet<int>();
        int droppedTriangles = 0;
        foreach (List<int> component in components)
        {
            if (component.Count >= triangles.Count)
                continue;

            var faces = component.Select(index => triangles[index]).ToList();
            if (!IsCone(faces, points))
                continue;

            droppedTriangles += component.Count;
            foreach (int index in component)
                drop.Add(index);
        }

        if (droppedTriangles == 0 || droppedTriangles >= triangles.Count)
            return false;

        var kept = new List<string>();
        for (int i = 0; i < triangles.Count; i++)
        {
            if (drop.Contains(i))
                continue;

            (int a, int b, int c) = triangles[i];
            kept.Add($"({a},{b},{c})");
        }

        var rewrittenArgs = args.ToList();
        rewrittenArgs[indexArg] = "(" + string.Join(",", kept) + ")";
        rewritten = $"#{faceset.Id}=IFCTRIANGULATEDFACESET({string.Join(",", rewrittenArgs)});";
        return true;
    }

    internal static bool IsCone(
        IReadOnlyList<(int A, int B, int C)> triangles,
        IReadOnlyList<(double X, double Y, double Z)> points)
    {
        if (triangles.Count is < 4 or > 96)
            return false;

        var incidence = new Dictionary<int, int>();
        foreach ((int a, int b, int c) in triangles)
        {
            Count(incidence, a);
            Count(incidence, b);
            Count(incidence, c);
        }

        if (incidence.Count < 4)
            return false;

        foreach (int apex in incidence.Keys)
        {
            if (apex < 1 || apex > points.Count || incidence[apex] < 3)
                continue;

            var side = new List<(int A, int B, int C)>();
            var floor = new List<(int A, int B, int C)>();
            foreach ((int a, int b, int c) in triangles)
            {
                if (a == apex || b == apex || c == apex)
                    side.Add((a, b, c));
                else
                    floor.Add((a, b, c));
            }

            if (side.Count < 3)
                continue;

            if (floor.Count == 0)
            {
                if (triangles.Count == 4 && incidence.Count == 4)
                    return true;
                continue;
            }

            var floorIds = new HashSet<int>();
            foreach ((int a, int b, int c) in floor)
            {
                floorIds.Add(a);
                floorIds.Add(b);
                floorIds.Add(c);
            }

            if (floorIds.Count < 3 || floorIds.Contains(apex))
                continue;

            List<int> ids = floorIds.Where(id => id >= 1 && id <= points.Count).ToList();
            if (ids.Count < 3 || !TryPlane(points[ids[0] - 1], points[ids[1] - 1], points[ids[2] - 1], out double nx, out double ny, out double nz, out double plane))
                continue;

            bool coplanar = true;
            foreach (int id in ids)
            {
                (double x, double y, double z) = points[id - 1];
                if (Math.Abs((nx * x) + (ny * y) + (nz * z) - plane) > 1e-3)
                {
                    coplanar = false;
                    break;
                }
            }

            if (!coplanar)
                continue;

            (double ax, double ay, double az) = points[apex - 1];
            if (Math.Abs((nx * ax) + (ny * ay) + (nz * az) - plane) > 1e-3)
                return true;
        }

        return false;
    }

    private static bool TryPlane(
        (double X, double Y, double Z) a,
        (double X, double Y, double Z) b,
        (double X, double Y, double Z) c,
        out double nx,
        out double ny,
        out double nz,
        out double plane)
    {
        double ux = b.X - a.X;
        double uy = b.Y - a.Y;
        double uz = b.Z - a.Z;
        double vx = c.X - a.X;
        double vy = c.Y - a.Y;
        double vz = c.Z - a.Z;
        nx = (uy * vz) - (uz * vy);
        ny = (uz * vx) - (ux * vz);
        nz = (ux * vy) - (uy * vx);
        double length = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        if (length < 1e-9)
        {
            plane = 0;
            return false;
        }

        nx /= length;
        ny /= length;
        nz /= length;
        plane = (nx * a.X) + (ny * a.Y) + (nz * a.Z);
        return true;
    }

    private static void Count(Dictionary<int, int> incidence, int index)
    {
        incidence.TryGetValue(index, out int count);
        incidence[index] = count + 1;
    }

    private static List<List<int>> Components(IReadOnlyList<(int A, int B, int C)> triangles)
    {
        int[] parent = Enumerable.Range(0, triangles.Count).ToArray();
        int Find(int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }

            return index;
        }

        var owners = new Dictionary<int, int>();
        for (int i = 0; i < triangles.Count; i++)
        {
            void Join(int vertex)
            {
                if (owners.TryGetValue(vertex, out int other))
                    parent[Find(i)] = Find(other);
                else
                    owners[vertex] = i;
            }

            Join(triangles[i].A);
            Join(triangles[i].B);
            Join(triangles[i].C);
        }

        return Enumerable.Range(0, triangles.Count)
            .GroupBy(Find)
            .Select(group => group.ToList())
            .ToList();
    }

    private static bool TryLoadPoints(
        Dictionary<int, Entity> entities,
        IReadOnlyList<string> args,
        out List<(double X, double Y, double Z)> points)
    {
        points = [];
        foreach (string arg in args)
        {
            if (!TryParseRef(arg, out int id) || !entities.TryGetValue(id, out Entity entity))
                continue;

            if (entity.Type != "IFCCARTESIANPOINTLIST3D")
                continue;

            foreach (Match match in Point().Matches(entity.Inner))
            {
                if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
                    !double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double y) ||
                    !double.TryParse(match.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                {
                    return false;
                }

                points.Add((x, y, z));
            }

            return points.Count > 0;
        }

        return false;
    }

    private static bool TryParseTriangles(string indexArg, out List<(int A, int B, int C)> triangles)
    {
        triangles = [];
        foreach (Match match in Triangle().Matches(indexArg))
        {
            triangles.Add((
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture)));
        }

        return triangles.Count > 0;
    }

    private static void CollectFacesets(
        Dictionary<int, Entity> entities,
        int id,
        HashSet<int> seen,
        HashSet<int> facesets)
    {
        if (!seen.Add(id) || !entities.TryGetValue(id, out Entity entity))
            return;

        if (entity.Type == "IFCTRIANGULATEDFACESET")
        {
            facesets.Add(id);
            return;
        }

        foreach (string arg in IfcColorPatcher.SplitArgs(entity.Inner))
        {
            foreach (int child in ParseRefs(arg))
                CollectFacesets(entities, child, seen, facesets);
        }
    }

    private static bool IsProtectedProduct(string type)
    {
        if (type.EndsWith("FITTING", StringComparison.Ordinal) ||
            type.EndsWith("SEGMENT", StringComparison.Ordinal))
        {
            return true;
        }

        return type is "IFCFLOWFITTING" or "IFCFLOWSEGMENT";
    }

    private static IEnumerable<int> ParseRefs(string token)
    {
        foreach (string part in IfcColorPatcher.SplitArgs(Unwrap(token)))
        {
            if (TryParseRef(part, out int id))
                yield return id;
        }
    }

    private static string Unwrap(string token)
    {
        token = token.Trim();
        if (token.StartsWith('(') && token.EndsWith(')'))
            return token[1..^1];
        return token;
    }

    private static bool TryParseRef(string token, out int id)
    {
        token = token.Trim();
        if (token.Length < 2 || token[0] != '#')
        {
            id = 0;
            return false;
        }

        return int.TryParse(token.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id != 0;
    }

    private static int PatchFile(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Encoding encoding = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? Encoding.UTF8
            : Encoding.GetEncoding(28591);
        string original = encoding.GetString(bytes);
        string patched = Strip(original);
        if (patched == original)
            return 0;

        File.WriteAllBytes(path, encoding.GetBytes(patched));
        return 1;
    }

    private static int ApplyZip(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        ZipArchiveEntry? entry = zip.Entries.FirstOrDefault(item =>
            item.Name.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return 0;

        string temp = Path.Combine(Path.GetTempPath(), "MetroCIM-cone-" + Guid.NewGuid().ToString("N") + ".ifc");
        entry.ExtractToFile(temp, overwrite: true);
        try
        {
            int patched = PatchFile(temp);
            if (patched == 0)
                return 0;

            string name = entry.FullName;
            entry.Delete();
            zip.CreateEntryFromFile(temp, name);
            return patched;
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
        }
    }

    private readonly record struct Entity(int Id, string Type, string Inner, int LineIndex);

    [GeneratedRegex(@"^#(\d+)\s*=\s*([A-Z][A-Z0-9]*)\s*\((.*)\)\s*;\s*$", RegexOptions.Compiled)]
    private static partial Regex EntityLine();

    [GeneratedRegex(@"\(([+-]?\d+(?:\.\d*)?(?:E[+-]?\d+)?),([+-]?\d+(?:\.\d*)?(?:E[+-]?\d+)?),([+-]?\d+(?:\.\d*)?(?:E[+-]?\d+)?)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex Point();

    [GeneratedRegex(@"\((\d+),(\d+),(\d+)\)", RegexOptions.Compiled)]
    private static partial Regex Triangle();
}

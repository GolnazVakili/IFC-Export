using System.Numerics;

namespace MetroCIM.Models;

public sealed class TriangleMesh
{
    public List<Vector3> Positions { get; } = [];
    public List<Vector3> Normals { get; } = [];
    public List<int> Indices { get; } = [];

    public bool IsEmpty => Indices.Count < 3;

    public bool IsPaperThin
    {
        get
        {
            if (Positions.Count == 0)
                return true;

            float minX = Positions[0].X, maxX = minX;
            float minY = Positions[0].Y, maxY = minY;
            float minZ = Positions[0].Z, maxZ = minZ;
            foreach (Vector3 point in Positions)
            {
                minX = Math.Min(minX, point.X);
                maxX = Math.Max(maxX, point.X);
                minY = Math.Min(minY, point.Y);
                maxY = Math.Max(maxY, point.Y);
                minZ = Math.Min(minZ, point.Z);
                maxZ = Math.Max(maxZ, point.Z);
            }

            float thin = Math.Min(maxX - minX, Math.Min(maxY - minY, maxZ - minZ));
            float longest = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
            return thin < 0.02f && longest > 0.15f;
        }
    }

    public void AddTriangle(Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 n = Vector3.Cross(b - a, c - a);
        if (n.LengthSquared() < 1e-20f)
            return;

        n = Vector3.Normalize(n);
        int i = Positions.Count;
        Positions.Add(a);
        Positions.Add(b);
        Positions.Add(c);
        Normals.Add(n);
        Normals.Add(n);
        Normals.Add(n);
        Indices.Add(i);
        Indices.Add(i + 1);
        Indices.Add(i + 2);
    }

    public void Append(TriangleMesh other)
    {
        int offset = Positions.Count;
        Positions.AddRange(other.Positions);
        Normals.AddRange(other.Normals);
        foreach (int index in other.Indices)
            Indices.Add(index + offset);
    }
}

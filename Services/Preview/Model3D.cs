using System;
using System.Numerics;

namespace ClankerExplorer.Services.Preview;

public readonly struct BoundingBox3D
{
    public Vector3 Min { get; }
    public Vector3 Max { get; }
    public Vector3 Center => (Min + Max) * 0.5f;
    public Vector3 Size => Max - Min;
    public float BoundingRadius => (Max - Min).Length() * 0.5f;

    public BoundingBox3D(Vector3 min, Vector3 max)
    {
        Min = min;
        Max = max;
    }

    public static BoundingBox3D CreateEmpty() =>
        new(new Vector3(float.MaxValue), new Vector3(float.MinValue));

    public BoundingBox3D Encapsulate(Vector3 point)
    {
        return new BoundingBox3D(
            Vector3.Min(Min, point),
            Vector3.Max(Max, point)
        );
    }
}

public readonly struct Triangle3D
{
    public Vector3 V0 { get; }
    public Vector3 V1 { get; }
    public Vector3 V2 { get; }
    public Vector3 Normal { get; }

    public Triangle3D(Vector3 v0, Vector3 v1, Vector3 v2, Vector3 normal)
    {
        V0 = v0;
        V1 = v1;
        V2 = v2;

        if (normal.LengthSquared() < 1e-6f)
        {
            var edge1 = v1 - v0;
            var edge2 = v2 - v0;
            var cross = Vector3.Cross(edge1, edge2);
            float len = cross.Length();
            Normal = len > 1e-6f ? cross / len : Vector3.UnitZ;
        }
        else
        {
            Normal = Vector3.Normalize(normal);
        }
    }
}

public class Model3D
{
    public Triangle3D[] Triangles { get; }
    public int TriangleCount => Triangles.Length;
    public BoundingBox3D Bounds { get; }

    public float Width => Bounds.Size.X;
    public float Depth => Bounds.Size.Y;
    public float Height => Bounds.Size.Z;

    public string FormattedDimensions =>
        $"{Width:F1} × {Depth:F1} × {Height:F1}";

    public string FormattedTriangleCount =>
        $"{TriangleCount:N0} triangles";

    public Model3D(Triangle3D[] triangles, BoundingBox3D bounds)
    {
        Triangles = triangles ?? Array.Empty<Triangle3D>();
        Bounds = bounds;
    }
}

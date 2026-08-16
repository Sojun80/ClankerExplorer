using System;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ClankerExplorer.Services.Preview;

public static class Rasterizer3D
{
    private static readonly Vector3 KeyLightDir = Vector3.Normalize(new Vector3(0.4f, 0.8f, 0.6f));
    private static readonly Vector3 FillLightDir = Vector3.Normalize(new Vector3(-0.6f, -0.3f, -0.5f));

    // Base mesh color: Crisp vibrant cyan-blue with warm highlight
    private const float BaseR = 0.15f;
    private const float BaseG = 0.65f;
    private const float BaseB = 0.95f;

    /// <summary>
    /// Renders a Model3D into an Avalonia WriteableBitmap using an optimized software rasterizer.
    /// </summary>
    public static WriteableBitmap RenderToBitmap(
        Model3D model,
        int width,
        int height,
        float yawDegrees = 45f,
        float pitchDegrees = -30f,
        float zoom = 1.0f,
        Vector2 pan = default,
        bool wireframe = false)
    {
        width = Math.Clamp(width, 64, 4096);
        height = Math.Clamp(height, 64, 4096);

        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Avalonia.Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var buffer = bitmap.Lock())
        {
            RenderToBuffer(model, buffer.Address, buffer.RowBytes, width, height, yawDegrees, pitchDegrees, zoom, pan, wireframe);
        }

        return bitmap;
    }

    public static unsafe void RenderToBuffer(
        Model3D model,
        IntPtr destBuffer,
        int stride,
        int width,
        int height,
        float yawDegrees,
        float pitchDegrees,
        float zoom,
        Vector2 pan,
        bool wireframe)
    {
        if (model.TriangleCount == 0) return;

        // 1. Clear color and Z-buffer
        var zBuffer = new float[width * height];
        for (int i = 0; i < zBuffer.Length; i++)
        {
            zBuffer[i] = float.MinValue;
        }

        byte* ptr = (byte*)destBuffer.ToPointer();

        // Clear background with sleek dark slate (#0B0F19)
        Parallel.For(0, height, y =>
        {
            uint* row = (uint*)(ptr + (y * stride));
            // Subtle subtle top-to-bottom background gradient
            float t = (float)y / height;
            byte r = (byte)(10 + (t * 4));
            byte g = (byte)(14 + (t * 6));
            byte b = (byte)(24 + (t * 8));
            uint bg = 0xFF000000 | ((uint)r << 16) | ((uint)g << 8) | b;

            for (int x = 0; x < width; x++)
            {
                row[x] = bg;
            }
        });

        // 2. Setup transforms
        var center = model.Bounds.Center;
        float maxDim = Math.Max(model.Bounds.Size.X, Math.Max(model.Bounds.Size.Y, model.Bounds.Size.Z));
        if (maxDim < 1e-5f) maxDim = 1.0f;

        float fitScale = (Math.Min(width, height) * 0.44f / maxDim) * Math.Clamp(zoom, 0.05f, 50.0f);

        float yawRad = yawDegrees * (MathF.PI / 180f);
        float pitchRad = pitchDegrees * (MathF.PI / 180f);

        var rot = Matrix4x4.CreateRotationZ(0) *
                  Matrix4x4.CreateRotationY(yawRad) *
                  Matrix4x4.CreateRotationX(pitchRad);

        float centerX = (width * 0.5f) + pan.X;
        float centerY = (height * 0.5f) + pan.Y;

        // 3. Transform and project all triangles
        var triangles = model.Triangles;
        int triCount = triangles.Length;

        // Use batch parallel rendering for smooth 60fps performance
        int batchSize = 1024;
        int batchCount = (triCount + batchSize - 1) / batchSize;

        for (int b = 0; b < batchCount; b++)
        {
            int start = b * batchSize;
            int end = Math.Min(start + batchSize, triCount);

            for (int i = start; i < end; i++)
            {
                var tri = triangles[i];

                // Center vertices
                var p0 = Vector3.Transform(tri.V0 - center, rot);
                var p1 = Vector3.Transform(tri.V1 - center, rot);
                var p2 = Vector3.Transform(tri.V2 - center, rot);

                // Compute face normal in camera space
                var e1 = p1 - p0;
                var e2 = p2 - p0;
                var faceNorm = Vector3.Cross(e1, e2);
                float faceNormLen = faceNorm.Length();
                if (faceNormLen < 1e-6f) continue;
                faceNorm /= faceNormLen;

                // Backface culling (if face points away from viewer +Z)
                if (faceNorm.Z <= 0.0f && !wireframe) continue;

                // Project to screen space (Orthographic with depth)
                float x0 = centerX + (p0.X * fitScale);
                float y0 = centerY - (p0.Y * fitScale);
                float z0 = p0.Z * fitScale;

                float x1 = centerX + (p1.X * fitScale);
                float y1 = centerY - (p1.Y * fitScale);
                float z1 = p1.Z * fitScale;

                float x2 = centerX + (p2.X * fitScale);
                float y2 = centerY - (p2.Y * fitScale);
                float z2 = p2.Z * fitScale;

                // Frustum clip
                float minX = MathF.Min(x0, MathF.Min(x1, x2));
                float maxX = MathF.Max(x0, MathF.Max(x1, x2));
                float minY = MathF.Min(y0, MathF.Min(y1, y2));
                float maxY = MathF.Max(y0, MathF.Max(y1, y2));

                if (maxX < 0 || minX >= width || maxY < 0 || minY >= height) continue;

                // Shading calculation
                float keyDiff = MathF.Max(0.0f, Vector3.Dot(faceNorm, KeyLightDir));
                float fillDiff = MathF.Max(0.0f, Vector3.Dot(faceNorm, FillLightDir)) * 0.3f;
                float ambient = 0.28f;

                // Specular highlight
                var halfVec = Vector3.Normalize(KeyLightDir + Vector3.UnitZ);
                float spec = MathF.Pow(MathF.Max(0.0f, Vector3.Dot(faceNorm, halfVec)), 16f) * 0.35f;

                float intensity = ambient + (keyDiff * 0.62f) + fillDiff + spec;
                intensity = Math.Clamp(intensity, 0.1f, 1.25f);

                byte r = (byte)Math.Clamp((int)(BaseR * intensity * 255f), 0, 255);
                byte g = (byte)Math.Clamp((int)(BaseG * intensity * 255f), 0, 255);
                byte bCol = (byte)Math.Clamp((int)(BaseB * intensity * 255f), 0, 255);
                uint pixelColor = 0xFF000000 | ((uint)r << 16) | ((uint)g << 8) | bCol;

                // Rasterize triangle
                RasterizeTriangle(
                    x0, y0, z0,
                    x1, y1, z1,
                    x2, y2, z2,
                    pixelColor,
                    ptr, stride, width, height, zBuffer);
            }
        }
    }

    private static unsafe void RasterizeTriangle(
        float x0, float y0, float z0,
        float x1, float y1, float z1,
        float x2, float y2, float z2,
        uint color,
        byte* destPtr,
        int stride,
        int width,
        int height,
        float[] zBuffer)
    {
        // Bounding box
        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(x0, MathF.Min(x1, x2))));
        int maxX = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(x0, MathF.Max(x1, x2))));
        int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(y0, MathF.Min(y1, y2))));
        int maxY = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(y0, MathF.Max(y1, y2))));

        float denom = ((y1 - y2) * (x0 - x2)) + ((x2 - x1) * (y0 - y2));
        if (MathF.Abs(denom) < 1e-6f) return;
        float invDenom = 1.0f / denom;

        for (int y = minY; y <= maxY; y++)
        {
            float py = y + 0.5f;
            uint* row = (uint*)(destPtr + (y * stride));
            int zRowOffset = y * width;

            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f;

                float w0 = (((y1 - y2) * (px - x2)) + ((x2 - x1) * (py - y2))) * invDenom;
                if (w0 < 0) continue;

                float w1 = (((y2 - y0) * (px - x2)) + ((x0 - x2) * (py - y2))) * invDenom;
                if (w1 < 0) continue;

                float w2 = 1.0f - w0 - w1;
                if (w2 < 0) continue;

                float z = (w0 * z0) + (w1 * z1) + (w2 * z2);
                int zIdx = zRowOffset + x;

                if (z > zBuffer[zIdx])
                {
                    zBuffer[zIdx] = z;
                    row[x] = color;
                }
            }
        }
    }
}

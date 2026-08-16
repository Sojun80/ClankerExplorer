using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClankerExplorer.Services.Preview;

public class StlLoadResult
{
    public bool Success { get; }
    public Model3D? Model { get; }
    public string? ErrorMessage { get; }
    public bool IsBinary { get; }

    private StlLoadResult(bool success, Model3D? model, string? errorMessage, bool isBinary)
    {
        Success = success;
        Model = model;
        ErrorMessage = errorMessage;
        IsBinary = isBinary;
    }

    public static StlLoadResult Succeeded(Model3D model, bool isBinary) =>
        new(true, model, null, isBinary);

    public static StlLoadResult Failed(string error) =>
        new(false, null, error, false);
}

public static class StlLoader
{
    private const int HeaderSize = 80;
    private const int TriangleRecordSize = 50;
    private const uint MaxSensibleTriangles = 15_000_000;

    /// <summary>
    /// Asynchronously loads an STL model from file (auto-detecting Binary or ASCII).
    /// </summary>
    public static async Task<StlLoadResult> LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            return StlLoadResult.Failed("File not found.");
        }

        try
        {
            return await Task.Run(() =>
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
                long fileLength = fs.Length;

                if (fileLength < 15) // Minimal possible ASCII STL ("solid \n endsolid")
                {
                    return StlLoadResult.Failed("File is too small to be a valid STL.");
                }

                // Check if it's binary
                if (IsBinaryStl(fs, fileLength, out uint binaryTriangleCount))
                {
                    return LoadBinary(fs, binaryTriangleCount, cancellationToken);
                }

                // Otherwise parse as ASCII
                fs.Seek(0, SeekOrigin.Begin);
                return LoadAscii(fs, cancellationToken);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return StlLoadResult.Failed($"Error loading STL: {ex.Message}");
        }
    }

    private static bool IsBinaryStl(FileStream fs, long fileLength, out uint triangleCount)
    {
        triangleCount = 0;
        if (fileLength < 84) return false;

        byte[] headerBuffer = new byte[84];
        int read = fs.Read(headerBuffer, 0, 84);
        if (read < 84) return false;

        triangleCount = BitConverter.ToUInt32(headerBuffer, 80);
        long expectedLength = 84L + ((long)triangleCount * TriangleRecordSize);

        // If exact match with binary file structure, it's definitely binary STL
        if (fileLength == expectedLength)
        {
            return true;
        }

        // If file doesn't start with "solid", but has sensible triangle count matching size within tolerance
        string headerPrefix = Encoding.ASCII.GetString(headerBuffer, 0, Math.Min(read, 80)).TrimStart();
        if (!headerPrefix.StartsWith("solid", StringComparison.OrdinalIgnoreCase))
        {
            if (triangleCount > 0 && triangleCount < MaxSensibleTriangles && fileLength >= expectedLength)
            {
                return true;
            }
        }

        return false;
    }

    private static StlLoadResult LoadBinary(FileStream fs, uint triangleCount, CancellationToken cancellationToken)
    {
        if (triangleCount > MaxSensibleTriangles)
        {
            return StlLoadResult.Failed($"STL triangle count ({triangleCount:N0}) exceeds supported limits.");
        }

        fs.Seek(84, SeekOrigin.Begin);
        var triangles = new Triangle3D[triangleCount];
        var bounds = BoundingBox3D.CreateEmpty();

        const int bufferTriangles = 4096;
        byte[] buffer = new byte[bufferTriangles * TriangleRecordSize];
        int trianglesRead = 0;

        while (trianglesRead < triangleCount)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int toRead = (int)Math.Min(bufferTriangles, triangleCount - trianglesRead);
            int bytesToRead = toRead * TriangleRecordSize;
            int bytesRead = fs.Read(buffer, 0, bytesToRead);

            if (bytesRead < bytesToRead && bytesRead % TriangleRecordSize != 0)
            {
                // Truncated binary file
                return StlLoadResult.Failed("Binary STL is truncated or corrupted.");
            }

            int count = bytesRead / TriangleRecordSize;
            for (int i = 0; i < count; i++)
            {
                int offset = i * TriangleRecordSize;

                float nx = BitConverter.ToSingle(buffer, offset + 0);
                float ny = BitConverter.ToSingle(buffer, offset + 4);
                float nz = BitConverter.ToSingle(buffer, offset + 8);

                float v0x = BitConverter.ToSingle(buffer, offset + 12);
                float v0y = BitConverter.ToSingle(buffer, offset + 16);
                float v0z = BitConverter.ToSingle(buffer, offset + 20);

                float v1x = BitConverter.ToSingle(buffer, offset + 24);
                float v1y = BitConverter.ToSingle(buffer, offset + 28);
                float v1z = BitConverter.ToSingle(buffer, offset + 32);

                float v2x = BitConverter.ToSingle(buffer, offset + 36);
                float v2y = BitConverter.ToSingle(buffer, offset + 40);
                float v2z = BitConverter.ToSingle(buffer, offset + 44);

                var v0 = new Vector3(v0x, v0y, v0z);
                var v1 = new Vector3(v1x, v1y, v1z);
                var v2 = new Vector3(v2x, v2y, v2z);
                var norm = new Vector3(nx, ny, nz);

                bounds = bounds.Encapsulate(v0);
                bounds = bounds.Encapsulate(v1);
                bounds = bounds.Encapsulate(v2);

                triangles[trianglesRead + i] = new Triangle3D(v0, v1, v2, norm);
            }

            trianglesRead += count;
            if (count < toRead) break;
        }

        if (trianglesRead == 0)
        {
            return StlLoadResult.Failed("No valid triangles found in binary STL.");
        }

        if (trianglesRead < triangleCount)
        {
            Array.Resize(ref triangles, trianglesRead);
        }

        var model = new Model3D(triangles, bounds);
        return StlLoadResult.Succeeded(model, isBinary: true);
    }

    private static StlLoadResult LoadAscii(FileStream fs, CancellationToken cancellationToken)
    {
        var triangles = new List<Triangle3D>(4096);
        var bounds = BoundingBox3D.CreateEmpty();

        using var reader = new StreamReader(fs, Encoding.UTF8, true, 64 * 1024, leaveOpen: true);

        Vector3 normal = Vector3.Zero;
        Vector3[] vertices = new Vector3[3];
        int vertexIndex = 0;
        long lineCount = 0;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lineCount++;
            if ((lineCount & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var span = line.AsSpan().Trim();
            if (span.IsEmpty) continue;

            if (span.StartsWith("facet normal", StringComparison.OrdinalIgnoreCase))
            {
                var normSpan = span.Slice(12).Trim();
                normal = ParseVector3(normSpan);
                vertexIndex = 0;
            }
            else if (span.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
            {
                if (vertexIndex < 3)
                {
                    var vertSpan = span.Slice(6).Trim();
                    vertices[vertexIndex] = ParseVector3(vertSpan);
                    bounds = bounds.Encapsulate(vertices[vertexIndex]);
                    vertexIndex++;

                    if (vertexIndex == 3)
                    {
                        triangles.Add(new Triangle3D(vertices[0], vertices[1], vertices[2], normal));
                    }
                }
            }
            else if (span.StartsWith("endfacet", StringComparison.OrdinalIgnoreCase))
            {
                vertexIndex = 0;
                normal = Vector3.Zero;
            }
        }

        if (triangles.Count == 0)
        {
            return StlLoadResult.Failed("Unable to preview this STL file (no geometry found).");
        }

        var model = new Model3D(triangles.ToArray(), bounds);
        return StlLoadResult.Succeeded(model, isBinary: false);
    }

    private static Vector3 ParseVector3(ReadOnlySpan<char> span)
    {
        float x = 0, y = 0, z = 0;
        int state = 0;
        int start = 0;

        for (int i = 0; i <= span.Length; i++)
        {
            bool isEnd = i == span.Length;
            bool isWhitespace = !isEnd && char.IsWhiteSpace(span[i]);

            if (isWhitespace || isEnd)
            {
                if (i > start)
                {
                    var token = span.Slice(start, i - start);
                    if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                    {
                        if (state == 0) x = val;
                        else if (state == 1) y = val;
                        else if (state == 2) { z = val; break; }
                        state++;
                    }
                }
                start = i + 1;
            }
        }

        return new Vector3(x, y, z);
    }
}

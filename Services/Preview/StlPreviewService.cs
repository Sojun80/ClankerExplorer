using System;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace ClankerExplorer.Services.Preview;

public class StlPreviewService
{
    private static readonly Lazy<StlPreviewService> _instance = new(() => new StlPreviewService());
    public static StlPreviewService Instance => _instance.Value;

    public bool IsStlFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string ext = Path.GetExtension(path);
        return ext.Equals(".stl", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<StlLoadResult> LoadStlAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return await StlLoader.LoadAsync(filePath, cancellationToken);
    }

    public async Task<WriteableBitmap?> RenderPreviewAsync(
        Model3D model,
        int width,
        int height,
        float yaw = 45f,
        float pitch = -25f,
        float zoom = 1.0f,
        Vector2 pan = default,
        bool wireframe = false,
        CancellationToken cancellationToken = default)
    {
        if (model == null || model.TriangleCount == 0) return null;

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Rasterizer3D.RenderToBitmap(model, width, height, yaw, pitch, zoom, pan, wireframe);
        }, cancellationToken);
    }

    public async Task<Bitmap?> GenerateThumbnailAsync(string filePath, int targetSize, CancellationToken cancellationToken = default)
    {
        if (!IsStlFile(filePath) || !File.Exists(filePath)) return null;

        try
        {
            var loadRes = await StlLoader.LoadAsync(filePath, cancellationToken);
            if (!loadRes.Success || loadRes.Model == null) return null;

            int size = Math.Clamp(targetSize, 64, 512);
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return (Bitmap)Rasterizer3D.RenderToBitmap(loadRes.Model, size, size, 45f, -25f, 1.0f);
            }, cancellationToken);
        }
        catch
        {
            return null;
        }
    }
}

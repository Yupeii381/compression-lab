using CompressionLab.Core;
using CompressionLab.Web.Configuration;
using CompressionLab.Web.Storage;

namespace CompressionLab.Web.Services;

public sealed class ArchiveService(ArchiveEngine engine, LabPaths paths)
{
    public async Task<RestoredFile> RestoreAsync(Stream source, CancellationToken cancellationToken)
    {
        var archivePath = paths.Temporary(".upload");
        var restoredPath = paths.Temporary(".restored");

        try
        {
            await using (var output = new FileStream(archivePath, FileMode.CreateNew))
            {
                await CopyArchiveAsync(source, output, cancellationToken);
            }

            var header = await Task.Run(() => engine.Restore(archivePath, restoredPath, cancellationToken), cancellationToken);
            return new(restoredPath, Path.GetFileName(header.Name));
        }
        catch
        {
            if (File.Exists(restoredPath))
            {
                File.Delete(restoredPath);
            }

            throw;
        }
        finally
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
        }
    }

    private static async Task CopyArchiveAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[LabLimits.CopyBufferBytes];
        long total = 0;
        int count;

        while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += count;
            if (total > LabLimits.UploadBytes)
            {
                throw new ArgumentException("Лимит архива — 256 МиБ");
            }

            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }
}

public sealed record RestoredFile(string Path, string Name);

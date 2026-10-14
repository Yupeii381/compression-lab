using System.Security.Cryptography;
using System.Text;
using CompressionLab.Web.Configuration;
using CompressionLab.Web.Models;
using CompressionLab.Web.Storage;

namespace CompressionLab.Web.Services;

public sealed class FileService(LabRepository repository, LabPaths paths)
{
    public async Task<LabFile> UploadAsync(Stream source, string name, CancellationToken cancellationToken)
    {
        name = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(name) || name.Length > LabLimits.FileNameCharacters)
        {
            throw new ArgumentException("Укажите имя файла (до 240 символов)");
        }

        var id = Guid.NewGuid().ToString("N");
        var path = paths.Blob(id);
        var saved = false;

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;

            await using (var output = new FileStream(path, FileMode.CreateNew))
            {
                var buffer = new byte[LabLimits.CopyBufferBytes];
                int count;

                while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    length += count;
                    if (length > LabLimits.UploadBytes)
                    {
                        throw new ArgumentException("Лимит файла — 256 МиБ");
                    }

                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
            }

            var file = new LabFile(id, name, length, Convert.ToHexString(hash.GetHashAndReset()));
            repository.AddFile(file);
            saved = true;
            return file;
        }
        finally
        {
            if (!saved && File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    public async Task AddDemoAsync(CancellationToken cancellationToken)
    {
        const string line = "Compression Lab / Лаборатория сжатия — AAAAABBBBBCCCCCDDDDD\n";
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(line, 2000)));
        using var source = new MemoryStream(bytes, writable: false);
        await UploadAsync(source, "sample.txt", cancellationToken);
    }
}

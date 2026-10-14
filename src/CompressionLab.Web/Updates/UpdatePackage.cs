using System.IO.Compression;
using System.Text.Json;
using CompressionLab.Core;

namespace CompressionLab.Web.Updates;

public static class UpdatePackage
{
    public static async Task<string> ExtractAsync(string archive, string destination, string expectedVersion, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        using var zip = ZipFile.OpenRead(archive);
        ValidateEntries(zip, destination);

        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew);
            await CopyEntryAsync(input, output, entry.Length, cancellationToken);
        }

        var roots = Directory.GetFiles(destination, "release-info.json", SearchOption.AllDirectories);
        if (roots.Length != 1)
        {
            throw new InvalidDataException("В обновлении должно быть одно описание выпуска");
        }

        var root = Path.GetDirectoryName(roots[0])!;
        ReadReleaseInfo(root, expectedVersion);
        foreach (var required in new[] { "CompressionLab.Web.exe", "CompressionLab.Web.dll", "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "wwwroot/index.html" })
        {
            if (!File.Exists(Path.Combine(root, required)))
            {
                throw new InvalidDataException($"В выпуске отсутствует {required}");
            }
        }

        return root;
    }

    public static ReleaseInfo ReadReleaseInfo(string root, string? expectedVersion = null)
    {
        var path = Path.Combine(root, "release-info.json");
        if (new FileInfo(path).Length > 1024 * 1024)
        {
            throw new InvalidDataException("Описание выпуска слишком большое");
        }

        var info = JsonSerializer.Deserialize<ReleaseInfo>(File.ReadAllText(path), ArchiveEngine.Json)
            ?? throw new InvalidDataException("Нет описания выпуска");
        if (info.Product != UpdateRules.Product || info.Runtime != UpdateRules.Runtime ||
            info.Files is null || info.Files.Length is < 1 or > 10000 || (expectedVersion is not null && info.Version != expectedVersion))
        {
            throw new InvalidDataException("Обновление предназначено для другого приложения или версии");
        }

        UpdateRules.ParseVersion(info.Version);
        foreach (var file in info.Files)
        {
            ValidateRelativePath(file);
        }

        return info;
    }

    private static void ValidateEntries(ZipArchive zip, string destination)
    {
        if (zip.Entries.Count is < 1 or > 10000)
        {
            throw new InvalidDataException("Некорректное количество файлов обновления");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.TrimEnd('/');
            ValidateRelativePath(name);
            var target = Path.GetFullPath(Path.Combine(destination, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!UpdateRules.IsWithin(target, destination) || !paths.Add(target) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Недопустимый путь или ссылка в обновлении");
            }

            if (entry.Length < 0 || entry.Length > UpdateRules.MaxExpandedBytes - expanded)
            {
                throw new InvalidDataException("Распакованное обновление слишком большое");
            }

            expanded += entry.Length;
        }
    }

    private static async Task CopyEntryAsync(Stream input, Stream output, long expectedLength, CancellationToken cancellationToken)
    {
        var buffer = new byte[65536];
        long copied = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (count > expectedLength - copied)
            {
                throw new InvalidDataException("Размер файла в обновлении превышает заявленный");
            }

            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            copied += count;
        }

        if (copied != expectedLength)
        {
            throw new InvalidDataException("Повреждённый файл в обновлении");
        }
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\') || path.Contains(':') ||
            path.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new InvalidDataException("Недопустимый путь в описании обновления");
        }
    }
}

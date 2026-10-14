using System.Security.Cryptography;
using System.Text.Json;
using CompressionLab.Core;

namespace CompressionLab.Web.Updates;

/// <summary>Only downloads bytes. It cannot install files or start a process.</summary>
public sealed class UpdateClient(HttpClient client)
{
    public async Task<UpdateManifest> CheckAsync(string feed, CancellationToken cancellationToken)
    {
        var uri = UpdateRules.ValidateUrl(feed);
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        await CopyBoundedAsync(input, output, 65536, null, cancellationToken);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(output.ToArray(), ArchiveEngine.Json)
            ?? throw new InvalidDataException("Пустое описание обновления");
        UpdateRules.ValidateManifest(manifest);
        return manifest;
    }

    public async Task DownloadAsync(UpdateManifest manifest, string destination, Action<long> progress, CancellationToken cancellationToken)
    {
        UpdateRules.ValidateManifest(manifest);
        using var response = await client.GetAsync(UpdateRules.ValidateUrl(manifest.Url), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != manifest.Size)
        {
            throw new InvalidDataException("Размер загрузки не совпадает с описанием выпуска");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using (var output = new FileStream(destination, FileMode.CreateNew))
        {
            await CopyBoundedAsync(input, output, manifest.Size, progress, cancellationToken);
            if (output.Length != manifest.Size)
            {
                throw new InvalidDataException("Обновление скачалось не полностью");
            }
        }

        await using var downloaded = File.OpenRead(destination);
        var actual = await SHA256.HashDataAsync(downloaded, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(manifest.Sha256)))
        {
            throw new InvalidDataException("Контрольная сумма обновления не совпадает. Установка отменена");
        }
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long limit, Action<long>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[65536];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new InvalidDataException("Загрузка превышает допустимый размер");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            progress?.Invoke(total);
        }
    }
}

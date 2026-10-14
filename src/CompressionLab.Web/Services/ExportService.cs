using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CompressionLab.Core;
using CompressionLab.Web.Storage;

namespace CompressionLab.Web.Services;

public sealed class ExportService(LabRepository repository, CodecRegistry registry)
{
    public byte[] Json()
    {
        var snapshot = repository.Snapshot();
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            exported = DateTimeOffset.UtcNow,
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            cpuCount = Environment.ProcessorCount,
            codecs = registry.Descriptors,
            files = snapshot.Files,
            jobs = snapshot.Jobs
        }, ArchiveEngine.Json);
    }

    public byte[] Csv()
    {
        var snapshot = repository.Snapshot();
        var files = snapshot.Files.ToDictionary(file => file.Id);
        var csv = new StringBuilder("File,Chain,OriginalBytes,ArchiveBytes,EncodeMs,DecodeMs,Verified\r\n");

        foreach (var job in snapshot.Jobs.Where(job => job.Result is not null))
        {
            var result = job.Result!;
            csv.AppendJoin(',',
                Quote(files[job.FileId].Name),
                Quote(job.Chain.Name),
                result.OriginalBytes.ToString(CultureInfo.InvariantCulture),
                result.ArchiveBytes.ToString(CultureInfo.InvariantCulture),
                result.EncodeMs.ToString("F3", CultureInfo.InvariantCulture),
                result.DecodeMs.ToString("F3", CultureInfo.InvariantCulture),
                result.Verified.ToString());
            csv.Append("\r\n");
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
    }

    private static string Quote(string value)
    {
        // Prevent user-provided names from becoming formulas when opened in a spreadsheet.
        if (value.StartsWith('=') || value.StartsWith('+') || value.StartsWith('-') || value.StartsWith('@'))
        {
            value = "'" + value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}

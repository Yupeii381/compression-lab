using System.Text.RegularExpressions;

namespace CompressionLab.Web.Updates;

public sealed record UpdateManifest(int Format, string Version, string Runtime, string Url, string Sha256, long Size, string? Notes);
public sealed record UpdateConfiguration(string? ManifestUrl = null, bool CheckOnStartup = true);
public sealed record ReleaseInfo(string Product, string Version, string Runtime, string[] Files);
public sealed record UpdateStatus(string CurrentVersion, string State, string Message,
    string? LatestVersion = null, string? Notes = null, long DownloadedBytes = 0, long? TotalBytes = null);
public sealed record UpdatePlan(int ParentId, long ParentStartedUtcTicks, string Installation, string PreparedInstallation,
    string Backup, string DataDirectory, int Port, bool OpenBrowser, string? ManifestUrl, string Version);

public static partial class UpdateRules
{
    public const string Product = "CompressionLab";
    public const string Runtime = "win-x64";
    public const long MaxPackageBytes = 512L * 1024 * 1024;
    public const long MaxExpandedBytes = 1024L * 1024 * 1024;
    public static string CurrentVersion => typeof(UpdateRules).Assembly.GetName().Version!.ToString(3);

    public static Version ParseVersion(string value)
    {
        if (value is null || !StableVersion().IsMatch(value) || !Version.TryParse(value, out var version))
        {
            throw new InvalidDataException("Версия обновления должна иметь вид 1.2.3");
        }

        return version;
    }

    public static Uri ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            throw new InvalidDataException("Для обновлений нужен HTTPS-адрес (HTTP разрешён только для локальной проверки)");
        }

        return uri;
    }

    public static void ValidateManifest(UpdateManifest manifest)
    {
        if (manifest.Format != 1 || manifest.Runtime != Runtime || manifest.Size is < 1 or > MaxPackageBytes ||
            manifest.Sha256 is null || !Sha256().IsMatch(manifest.Sha256) || manifest.Notes?.Length > 8000)
        {
            throw new InvalidDataException("Некорректное описание обновления");
        }

        ParseVersion(manifest.Version);
        ValidateUrl(manifest.Url);
    }

    public static bool IsWithin(string path, string directory) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")]
    private static partial Regex StableVersion();

    [GeneratedRegex(@"^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256();
}

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CompressionLab.Core;
using CompressionLab.Web.Updates;

internal static class UpdateTests
{
    public static void Run(string folder, Action<bool, string> check, Action<Action, string> reject)
    {
        var manifest = new UpdateManifest(1, "9.2.0", "win-x64", "https://example.org/update.zip", new string('A', 64), 3, "Notes");
        UpdateRules.ValidateManifest(manifest);
        check(UpdateRules.ParseVersion("1.10.0") > UpdateRules.ParseVersion("1.9.0"), "Numeric version comparison");
        reject(() => UpdateRules.ParseVersion("v1.2.0"), "Invalid release version accepted");
        reject(() => UpdateRules.ParseVersion("1.2.0-preview"), "Prerelease silently installed");
        reject(() => UpdateRules.ValidateUrl("http://example.org/update.zip"), "Insecure remote update accepted");
        reject(() => UpdateRules.ValidateUrl("file:///C:/update.zip"), "Local file protocol accepted");
        reject(() => UpdateRules.ValidateManifest(manifest with { Runtime = "linux-x64" }), "Wrong platform accepted");
        reject(() => UpdateRules.ValidateManifest(manifest with { Sha256 = "bad" }), "Invalid checksum accepted");
        reject(() => UpdateRules.ValidateManifest(manifest with { Size = UpdateRules.MaxPackageBytes + 1 }), "Oversized package accepted");
        check(UpdateRules.ValidateUrl("http://127.0.0.1:9999/feed.json").IsLoopback, "Local integration feed permitted");

        var bytes = new byte[] { 1, 2, 3 };
        manifest = manifest with { Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
        using var http = new HttpClient(new FakeHandler(request => request.RequestUri!.AbsolutePath.EndsWith("feed.json")
            ? JsonSerializer.SerializeToUtf8Bytes(manifest, ArchiveEngine.Json) : bytes));
        var client = new UpdateClient(http);
        var parsed = client.CheckAsync("https://example.org/feed.json", default).GetAwaiter().GetResult();
        check(parsed.Version == manifest.Version, "Manifest download");
        var path = Path.Combine(folder, "update.zip");
        long downloaded = 0;
        client.DownloadAsync(manifest, path, count => downloaded = count, default).GetAwaiter().GetResult();
        check(downloaded == 3 && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes), "Stream download and progress");
        reject(() => client.DownloadAsync(manifest with { Sha256 = new string('B', 64) }, path + ".corrupt", _ => { }, default).GetAwaiter().GetResult(), "Wrong package checksum accepted");
        reject(() => client.DownloadAsync(manifest with { Size = 4 }, path + ".short", _ => { }, default).GetAwaiter().GetResult(), "Partial download accepted");

        using var oversizedHttp = new HttpClient(new FakeHandler(_ => new byte[65537]));
        reject(() => new UpdateClient(oversizedHttp).CheckAsync("https://example.org/feed.json", default).GetAwaiter().GetResult(), "Unbounded manifest accepted");

        TestPackages(folder, check, reject);
    }

    private static void TestPackages(string folder, Action<bool, string> check, Action<Action, string> reject)
    {
        var valid = Path.Combine(folder, "valid-update.zip");
        var required = new[] { "CompressionLab.Web.exe", "CompressionLab.Web.dll", "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "wwwroot/index.html" };
        using (var archive = ZipFile.Open(valid, ZipArchiveMode.Create))
        {
            foreach (var file in required) WriteEntry(archive, "release/" + file, "test content");
            WriteEntry(archive, "release/release-info.json", JsonSerializer.Serialize(new ReleaseInfo("CompressionLab", "9.2.0", "win-x64", required), ArchiveEngine.Json));
        }

        var extracted = UpdatePackage.ExtractAsync(valid, Path.Combine(folder, "valid-extracted"), "9.2.0", default).GetAwaiter().GetResult();
        check(File.Exists(Path.Combine(extracted, "CompressionLab.Web.exe")), "Valid package extraction");
        reject(() => UpdatePackage.ExtractAsync(valid, Path.Combine(folder, "wrong-version"), "9.3.0", default).GetAwaiter().GetResult(), "Package version mismatch accepted");

        foreach (var badPath in new[] { "../escaped.txt", "/escaped.txt", "C:/escaped.txt", "release/file:stream", "release/./file", "release\\file" })
        {
            var id = Guid.NewGuid().ToString("N");
            var package = Path.Combine(folder, id + ".zip");
            using (var archive = ZipFile.Open(package, ZipArchiveMode.Create)) WriteEntry(archive, badPath, "bad");
            reject(() => UpdatePackage.ExtractAsync(package, Path.Combine(folder, id), "9.2.0", default).GetAwaiter().GetResult(), "Unsafe ZIP path accepted: " + badPath);
        }

        var duplicate = Path.Combine(folder, "duplicate.zip");
        using (var archive = ZipFile.Open(duplicate, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "Release/A.dll", "a");
            WriteEntry(archive, "release/a.dll", "b");
        }
        reject(() => UpdatePackage.ExtractAsync(duplicate, Path.Combine(folder, "duplicate"), "9.2.0", default).GetAwaiter().GetResult(), "Windows case-insensitive duplicate accepted");
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, byte[]> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(response(request)) });
    }
}

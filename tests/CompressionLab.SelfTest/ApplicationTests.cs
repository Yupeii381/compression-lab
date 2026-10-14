using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CompressionLab.Core;
using CompressionLab.Web.Configuration;
using CompressionLab.Web.Models;
using CompressionLab.Web.Services;
using CompressionLab.Web.Storage;

internal static class ApplicationTests
{
    public static void Run(CodecRegistry registry, string folder, Action<bool, string> check, Action<Action, string> reject)
    {
        TestStartupOptions(folder, check, reject);
        TestQueueAndPersistence(registry, folder, check, reject);
        TestLegacyArchive(registry, folder, check);
    }

    private static void TestStartupOptions(string folder, Action<bool, string> check, Action<Action, string> reject)
    {
        var options = StartupOptions.Parse(["--port", "8099", "--no-browser", "--data-dir", folder]);
        check(options.Port == 8099 && options.Address == "http://127.0.0.1:8099", "Configurable loopback port");
        check(!options.OpenBrowser && options.DataDirectory == Path.GetFullPath(folder), "Explicit startup options");
        check(StartupOptions.Parse([]).OpenBrowser, "EXE opens browser by default");
        check(StartupOptions.Parse([]).BrowserAddress == "http://localhost/co-lab/" && StartupOptions.Parse([]).Port == 80,
            "Default local URL hides the standard HTTP port");
        check(options.BrowserAddress == "http://localhost:8099/co-lab/", "Custom port retains application path");
        reject(() => StartupOptions.Parse(["--port", "0"]), "Invalid port accepted");
        reject(() => StartupOptions.Parse(["--data-dir"]), "Missing startup value accepted");
        reject(() => StartupOptions.Parse(["--unknown"]), "Unknown startup option accepted");
    }

    private static void TestQueueAndPersistence(CodecRegistry registry, string folder, Action<bool, string> check, Action<Action, string> reject)
    {
        var paths = new LabPaths(Path.Combine(folder, "application"));
        var validator = new ExperimentValidator(registry);
        var bytes = Encoding.UTF8.GetBytes("persistent source");
        string fileId;
        string jobId;

        using (var repository = new LabRepository(paths))
        {
            reject(() => { using var duplicate = new LabRepository(paths); }, "Second host obtained directory lock");
            var files = new FileService(repository, paths);
            using var source = new MemoryStream(bytes);
            var file = files.UploadAsync(source, "sample.txt", default).GetAwaiter().GetResult();
            fileId = file.Id;
            check(file.Sha256 == Convert.ToHexString(SHA256.HashData(bytes)), "Upload hash");
            check(File.ReadAllBytes(paths.Blob(file.Id)).AsSpan().SequenceEqual(bytes), "Upload persistence");

            var request = new RunRequest([file.Id], [new("Gzip", [new("gzip", [])])]);
            var queue = new JobQueue(repository, validator);
            queue.TogglePause();
            queue.Enqueue(request);
            jobId = repository.Snapshot().Jobs.Single().Id;
            check(queue.TryStartNext(default) is null, "Paused queue started a job");

            queue.Cancel(jobId);
            check(repository.Snapshot().Jobs.Single().Status == JobStatus.Cancelled, "Cancel queued job");
            queue.Retry(jobId);
            check(repository.Snapshot().Jobs.Single().Status == JobStatus.Queued, "Retry queued job");
            queue.TogglePause();
            var active = queue.TryStartNext(default)!;
            check(active.Job.Id == jobId && queue.TryStartNext(default) is null, "One active worker");
            reject(queue.BeginUpdate, "Update started while an experiment is running");
            queue.Cancel(jobId);
            queue.Finish(active, null, new OperationCanceledException(), stopping: true);
            check(repository.Snapshot().Jobs.Single().Status == JobStatus.Cancelled, "User cancel must survive host shutdown");

            queue.Retry(jobId);
            active = queue.TryStartNext(default)!;
            queue.Finish(active, null, new OperationCanceledException(), stopping: true);
            check(repository.Snapshot().Jobs.Single().Status == JobStatus.Queued, "Shutdown should requeue unfinished work");
            queue.BeginUpdate();
            check(queue.TryStartNext(default) is null, "Queue started work during update");
            reject(() => queue.Enqueue(request), "New job accepted during update");
            queue.EndUpdate();

            // Simulate a process crash after its running state was persisted.
            repository.TryStartNext();
            queue.TogglePause();
            reject(() => validator.CreateJobs(request with { Chains = [null!] }), "Null chain accepted");
            reject(() => validator.CreateJobs(request with { BlockSize = 0 }), "Invalid block accepted");
            reject(() => paths.Blob("../outside"), "Path traversal accepted");

            var normalized = validator.CreateJobs(request).Single();
            check(normalized.Chain.Steps[0].Version == "1" && normalized.Chain.Steps[0].Options["level"] == 2, "Codec options normalized");
            request.Chains[0].Steps[0].Options["level"] = 3;
            check(normalized.Chain.Steps[0].Options["level"] == 2, "Request mutation affected queued options");
        }

        using (var reopened = new LabRepository(paths))
        {
            var snapshot = reopened.Snapshot();
            check(snapshot.Paused, "Pause lost after restart");
            check(snapshot.Files.Single().Id == fileId && snapshot.Jobs.Single().Id == jobId, "IDs lost after restart");
            check(snapshot.Jobs.Single().Status == JobStatus.Queued, "Crashed running job not requeued");
            snapshot.Jobs[0] = snapshot.Jobs[0] with { Status = JobStatus.Failed };
            check(reopened.Snapshot().Jobs[0].Status == JobStatus.Queued, "Snapshot array exposes repository mutation");

            var exports = new ExportService(reopened, registry);
            using var json = JsonDocument.Parse(exports.Json());
            check(json.RootElement.GetProperty("files").GetArrayLength() == 1, "Export snapshot");
        }
    }

    private static void TestLegacyArchive(CodecRegistry registry, string folder, Action<bool, string> check)
    {
        var originalArchive = Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-rle-lzma.clcs");
        var restored = Path.Combine(folder, "legacy-restored.txt");
        new ArchiveEngine(registry).Restore(originalArchive, restored, default);
        using var input = File.OpenRead(restored);
        check(Convert.ToHexString(SHA256.HashData(input)) == "BE22E5A6A253F65B7E0CE796DEC6B1B0D30C4779914394833DBB80A32FFC392A",
            "Archive from before refactoring must remain readable");
    }
}

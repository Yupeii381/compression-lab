using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CompressionLab.Core;
using CompressionLab.Web.Configuration;
using CompressionLab.Web.Services;

namespace CompressionLab.Web.Updates;

public sealed class UpdateService(
    UpdateClient client, UpdateConfiguration configuration, StartupOptions options,
    JobQueue queue, IHostApplicationLifetime lifetime, ILogger<UpdateService> logger)
{
    private readonly object gate = new();
    private UpdateManifest? latest;
    private UpdateStatus status = InitialStatus(configuration, options);

    public UpdateStatus Status { get { lock (gate) return status; } }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (configuration.ManifestUrl is null || status.State is "checking" or "downloading" or "installing")
            {
                return;
            }

            status = new(UpdateRules.CurrentVersion, "checking", "Проверяем последнюю версию…");
            latest = null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var manifest = await client.CheckAsync(configuration.ManifestUrl!, timeout.Token);
            lock (gate)
            {
                latest = manifest;
                var available = UpdateRules.ParseVersion(manifest.Version) > UpdateRules.ParseVersion(UpdateRules.CurrentVersion);
                status = new(UpdateRules.CurrentVersion, available ? "available" : "current",
                    available ? $"Доступна версия {manifest.Version}" : "Установлена актуальная версия",
                    manifest.Version, manifest.Notes, TotalBytes: manifest.Size);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Update check failed");
            SetError("Не удалось проверить обновления. " + exception.Message);
        }
    }

    public void StartInstallation()
    {
        UpdateManifest manifest;
        lock (gate)
        {
            if (status.State != "available" || latest is null)
            {
                throw new ArgumentException("Сначала проверьте наличие новой версии");
            }

            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
                !File.Exists(Path.Combine(AppContext.BaseDirectory, "release-info.json")))
            {
                throw new ArgumentException("Автоматическая установка доступна в готовом выпуске Windows x64");
            }

            var installation = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            if (UpdateRules.IsWithin(options.DataDirectory, installation) ||
                string.Equals(options.DataDirectory, installation, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Для автообновления каталог данных должен находиться вне папки приложения");
            }

            queue.BeginUpdate();
            manifest = latest;
            status = status with { State = "downloading", Message = "Скачиваем и проверяем обновление…", DownloadedBytes = 0 };
        }

        _ = Task.Run(() => InstallAsync(manifest));
    }

    private async Task InstallAsync(UpdateManifest manifest)
    {
        var work = Path.Combine(options.DataDirectory, ".updates", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(work);
            var archive = Path.Combine(work, "update.zip");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            await client.DownloadAsync(manifest, archive, downloaded =>
            {
                lock (gate) status = status with { DownloadedBytes = downloaded };
            }, timeout.Token);

            var prepared = await UpdatePackage.ExtractAsync(archive, Path.Combine(work, "extracted"), manifest.Version, timeout.Token);
            var installation = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            // Probe the parent directory before asking the current app to exit.
            var probe = Path.Combine(Path.GetDirectoryName(installation)!, ".compressionlab-update-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe)) { }
            File.Delete(probe);
            using var current = Process.GetCurrentProcess();
            var plan = new UpdatePlan(current.Id, current.StartTime.ToUniversalTime().Ticks, installation, prepared,
                installation + ".backup-" + Guid.NewGuid().ToString("N"), options.DataDirectory, options.Port,
                options.OpenBrowser, configuration.ManifestUrl, manifest.Version);
            var planPath = Path.Combine(work, "plan.json");
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan, ArchiveEngine.Json), timeout.Token);

            using var helper = UpdateInstaller.StartHelper(prepared, planPath);
            await Task.Delay(750, timeout.Token);
            if (helper.HasExited)
            {
                throw new IOException("Не удалось запустить установщик обновления");
            }

            lock (gate) status = status with { State = "installing", Message = "Обновление проверено. Перезапускаем приложение…" };
            lifetime.StopApplication();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Update installation failed; staging: {Directory}", work);
            queue.EndUpdate();
            SetError("Обновление не установлено. " + exception.Message);
        }
    }

    private void SetError(string message)
    {
        lock (gate) status = status with { State = "error", Message = message };
    }

    private static UpdateStatus InitialStatus(UpdateConfiguration configuration, StartupOptions options)
    {
        try
        {
            var resultPath = Path.Combine(options.DataDirectory, ".updates", "last-result.json");
            if (File.Exists(resultPath))
            {
                using var result = JsonDocument.Parse(File.ReadAllText(resultPath));
                if (result.RootElement.GetProperty("state").GetString() == "error")
                {
                    return new(UpdateRules.CurrentVersion, "error", result.RootElement.GetProperty("message").GetString()!);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException) { }

        return new(UpdateRules.CurrentVersion, configuration.ManifestUrl is null ? "unconfigured" : "idle",
            configuration.ManifestUrl is null ? "Источник обновлений не настроен" : "Можно проверить наличие обновлений");
    }
}

using System.Diagnostics;
using System.Text.Json;
using CompressionLab.Core;
using CompressionLab.Web.Configuration;

namespace CompressionLab.Web.Updates;

/// <summary>Runs from the verified staging folder after the old host exits. Keeps the old release for rollback.</summary>
public static class UpdateInstaller
{
    public static Process StartHelper(string prepared, string planPath)
    {
        var start = new ProcessStartInfo(Path.Combine(prepared, "CompressionLab.Web.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = prepared
        };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(planPath);
        return Process.Start(start) ?? throw new IOException("Не удалось запустить установку обновления");
    }

    public static async Task<int> RunAsync(string planPath)
    {
        UpdatePlan? plan = null;
        var replaced = false;
        Process? started = null;
        var validated = false;

        try
        {
            plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(planPath), ArchiveEngine.Json)
                ?? throw new InvalidDataException("Пустой план обновления");
            ValidatePlan(plan, planPath);
            validated = true;
            await WaitForParentAsync(plan);

            var oldInfo = UpdatePackage.ReadReleaseInfo(plan.Installation);
            var newInfo = UpdatePackage.ReadReleaseInfo(plan.PreparedInstallation, plan.Version);
            Directory.Move(plan.Installation, plan.Backup);
            replaced = true;
            CopyDirectory(plan.PreparedInstallation, plan.Installation);
            PreserveCustomPlugins(plan, oldInfo, newInfo);
            SaveConfiguration(plan);

            started = StartApplication(plan, plan.Installation);
            await WaitForReadyAsync(started, plan.Port, plan.Version);
            SaveResult(plan, "updated", $"Установлена версия {plan.Version}");
            return 0;
        }
        catch (Exception exception)
        {
            if (started is not null && !started.HasExited)
            {
                started.Kill();
                await started.WaitForExitAsync();
            }

            if (plan is not null && validated)
            {
                try
                {
                    if (replaced)
                    {
                        // Keep failed files for diagnostics instead of recursively deleting an installation.
                        if (Directory.Exists(plan.Installation))
                        {
                            Directory.Move(plan.Installation, plan.Installation + ".failed-" + Guid.NewGuid().ToString("N"));
                        }

                        Directory.Move(plan.Backup, plan.Installation);
                    }

                    SaveResult(plan, "error", $"Обновление не установлено; прежняя версия сохранена. {exception.Message}");
                    if (replaced)
                    {
                        StartApplication(plan, plan.Installation).Dispose();
                    }
                }
                catch (Exception rollbackError)
                {
                    Console.Error.WriteLine($"Резервная копия: {plan.Backup}. {rollbackError.Message}");
                }
            }

            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        finally
        {
            started?.Dispose();
        }
    }

    public static void ValidatePlan(UpdatePlan plan, string planPath)
    {
        var updateRoot = Path.Combine(plan.DataDirectory, ".updates");
        if (!UpdateRules.IsWithin(planPath, updateRoot) || !UpdateRules.IsWithin(plan.PreparedInstallation, updateRoot) ||
            !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.PreparedInstallation)),
                Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase) ||
            Path.GetDirectoryName(plan.Backup) != Path.GetDirectoryName(plan.Installation) ||
            !plan.Backup.StartsWith(plan.Installation + ".backup-", StringComparison.OrdinalIgnoreCase) ||
            Directory.Exists(plan.Backup) || UpdateRules.IsWithin(plan.DataDirectory, plan.Installation) ||
            string.Equals(plan.DataDirectory, plan.Installation, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Некорректные каталоги установки обновления");
        }

        UpdateRules.ParseVersion(plan.Version);
        UpdatePackage.ReadReleaseInfo(plan.Installation);
        UpdatePackage.ReadReleaseInfo(plan.PreparedInstallation, plan.Version);
    }

    private static async Task WaitForParentAsync(UpdatePlan plan)
    {
        Process parent;
        try { parent = Process.GetProcessById(plan.ParentId); }
        catch (ArgumentException) { return; }

        using (parent)
        {
            if (parent.HasExited)
            {
                return;
            }

            if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartedUtcTicks ||
                !string.Equals(parent.MainModule?.FileName, Path.Combine(plan.Installation, "CompressionLab.Web.exe"), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Не совпадает процесс обновляемого приложения");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await parent.WaitForExitAsync(timeout.Token);
        }
    }

    private static Process StartApplication(UpdatePlan plan, string directory)
    {
        var start = new ProcessStartInfo(Path.Combine(directory, "CompressionLab.Web.exe"))
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = !plan.OpenBrowser
        };
        foreach (var argument in new[] { "--port", plan.Port.ToString(), "--data-dir", plan.DataDirectory })
        {
            start.ArgumentList.Add(argument);
        }

        // Preserve normal console visibility while reconnecting the existing browser tab.
        start.ArgumentList.Add("--update-restart");
        if (!plan.OpenBrowser)
        {
            start.ArgumentList.Add("--no-browser");
        }

        if (plan.ManifestUrl is not null)
        {
            start.ArgumentList.Add("--update-feed");
            start.ArgumentList.Add(plan.ManifestUrl);
        }

        return Process.Start(start) ?? throw new IOException("Не удалось запустить приложение после обновления");
    }

    private static async Task WaitForReadyAsync(Process process, int port, string version)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new IOException($"Новая версия завершилась с кодом {process.ExitCode}");
            }

            try
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{port}/api/health");
                using var health = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (response.IsSuccessStatusCode && health.RootElement.GetProperty("processId").GetInt32() == process.Id &&
                    health.RootElement.GetProperty("version").GetString() == version)
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException) { }

            await Task.Delay(250);
        }

        throw new IOException("Новая версия не подтвердила успешный запуск");
    }

    private static void PreserveCustomPlugins(UpdatePlan plan, ReleaseInfo oldInfo, ReleaseInfo newInfo)
    {
        var oldPluginRoot = Path.Combine(plan.Backup, "plugins");
        if (!Directory.Exists(oldPluginRoot))
        {
            return;
        }

        var shipped = oldInfo.Files.Concat(newInfo.Files).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var source in Directory.EnumerateFiles(oldPluginRoot, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        }))
        {
            var relative = Path.GetRelativePath(plan.Backup, source).Replace('\\', '/');
            if (shipped.Contains(relative))
            {
                continue;
            }

            var target = Path.Combine(plan.Installation, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: false);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static void SaveConfiguration(UpdatePlan plan)
    {
        File.WriteAllText(Path.Combine(plan.Installation, "updates.json"),
            JsonSerializer.Serialize(new UpdateConfiguration(plan.ManifestUrl), ArchiveEngine.Json));
    }

    private static void SaveResult(UpdatePlan plan, string state, string message)
    {
        var path = Path.Combine(plan.DataDirectory, ".updates", "last-result.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { state, message, version = plan.Version }, ArchiveEngine.Json));
    }
}

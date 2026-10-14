using System.Text.Json;
using CompressionLab.Core;
using CompressionLab.Web.Configuration;
using CompressionLab.Web.Models;

namespace CompressionLab.Web.Storage;

/// <summary>Serializes state access and commits a new JSON file before replacing the in-memory snapshot.</summary>
public sealed class LabRepository : IDisposable
{
    private readonly object gate = new();
    private readonly LabPaths paths;
    private readonly FileStream hostLock;
    private LabSnapshot state;

    public LabRepository(LabPaths paths)
    {
        this.paths = paths;
        hostLock = new FileStream(paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        try
        {
            state = Load();
        }
        catch
        {
            hostLock.Dispose();
            throw;
        }
    }

    public LabSnapshot Snapshot()
    {
        lock (gate)
        {
            return state with { Files = state.Files.ToArray(), Jobs = state.Jobs.ToArray() };
        }
    }

    public LabFile GetFile(string id)
    {
        lock (gate)
        {
            return state.Files.FirstOrDefault(file => file.Id == id)
                ?? throw new ArgumentException("Файл не найден");
        }
    }

    public void AddFile(LabFile file)
    {
        lock (gate)
        {
            Commit(state with { Files = [.. state.Files, file] });
        }
    }

    public void Enqueue(Job[] jobs)
    {
        lock (gate)
        {
            if (jobs.Any(job => !state.Files.Any(file => file.Id == job.FileId)))
            {
                throw new ArgumentException("Файл не найден");
            }

            if (state.Jobs.Count(job => JobStatus.IsActive(job.Status)) + jobs.Length > LabLimits.ActiveJobs)
            {
                throw new ArgumentException("Очередь заполнена");
            }

            Commit(state with { Jobs = [.. state.Jobs, .. jobs] });
        }
    }

    public Job? TryStartNext()
    {
        lock (gate)
        {
            if (state.Paused)
            {
                return null;
            }

            var next = state.Jobs.FirstOrDefault(job => job.Status == JobStatus.Queued);
            if (next is null)
            {
                return null;
            }

            var running = next with { Status = JobStatus.Running, Error = null };
            ReplaceJob(running);
            return running;
        }
    }

    public void UpdateJob(string id, Func<Job, Job> update)
    {
        lock (gate)
        {
            var job = state.Jobs.FirstOrDefault(job => job.Id == id)
                ?? throw new ArgumentException("Задача не найдена");
            ReplaceJob(update(job));
        }
    }

    public void TogglePause()
    {
        lock (gate)
        {
            Commit(state with { Paused = !state.Paused });
        }
    }

    private LabSnapshot Load()
    {
        if (!File.Exists(paths.StateFile))
        {
            return new([], [], false);
        }

        var saved = JsonSerializer.Deserialize<LabSnapshot>(File.ReadAllText(paths.StateFile), ArchiveEngine.Json)
            ?? throw new InvalidDataException("Повреждено состояние лаборатории");

        if (saved.Files is null || saved.Jobs is null)
        {
            throw new InvalidDataException("В состоянии лаборатории нет файлов или задач");
        }

        return saved with
        {
            Jobs = saved.Jobs.Select(job => job.Status == JobStatus.Running
                ? job with { Status = JobStatus.Queued }
                : job).ToArray()
        };
    }

    private void ReplaceJob(Job replacement)
    {
        Commit(state with
        {
            Jobs = state.Jobs.Select(job => job.Id == replacement.Id ? replacement : job).ToArray()
        });
    }

    private void Commit(LabSnapshot next)
    {
        var temporary = paths.StateFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(next, ArchiveEngine.Json));
        File.Move(temporary, paths.StateFile, overwrite: true);
        state = next;
    }

    public void Dispose() => hostLock.Dispose();
}

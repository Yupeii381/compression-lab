namespace CompressionLab.Web.Storage;

/// <summary>All persistent and temporary paths belong to one locked data directory.</summary>
public sealed class LabPaths
{
    public string Root { get; }
    public string StateFile => Path.Combine(Root, "state.json");
    public string LockFile => Path.Combine(Root, "host.lock");

    public LabPaths(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, "blobs"));
        Directory.CreateDirectory(Path.Combine(Root, "artifacts"));
    }

    public string Blob(string id) => Path.Combine(Root, "blobs", ValidateId(id));
    public string Artifact(string id) => Path.Combine(Root, "artifacts", ValidateId(id) + ".clcs");
    public string Temporary(string extension) => Path.Combine(Root, "blobs", Guid.NewGuid().ToString("N") + extension);

    private static string ValidateId(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
        {
            throw new ArgumentException("Некорректный идентификатор файла или задачи");
        }

        return id;
    }
}

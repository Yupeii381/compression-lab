namespace CompressionLab.Web.Configuration;

public static class LabLimits
{
    public const long UploadBytes = 256L * 1024 * 1024;
    public const int CopyBufferBytes = 65536;
    public const int FileNameCharacters = 240;
    public const int FilesPerRun = 100;
    public const int ChainsPerRun = 20;
    public const int ChainNameCharacters = 200;
    public const int ActiveJobs = 2000;
    public static readonly TimeSpan QueuePollInterval = TimeSpan.FromMilliseconds(300);
}

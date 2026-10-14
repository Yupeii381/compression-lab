namespace CompressionLab.Core;

public static class BenchmarkSettings
{
    public const int MinBlockBytes = 65536;
    public const int MaxBlockBytes = 4 * 1024 * 1024;
    public const int MaxSteps = 8;

    public static void Validate(int blockSize, int repeats, int warmup)
    {
        if (blockSize is < MinBlockBytes or > MaxBlockBytes || repeats is < 1 or > 10 || warmup is < 0 or > 3)
        {
            throw new ArgumentException("Блок: 64 КиБ–4 МиБ, повторы: 1–10, прогрев: 0–3");
        }
    }
}

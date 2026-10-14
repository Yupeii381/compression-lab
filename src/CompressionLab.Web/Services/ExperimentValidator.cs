using CompressionLab.Core;
using CompressionLab.Web.Configuration;
using CompressionLab.Web.Models;

namespace CompressionLab.Web.Services;

public sealed class ExperimentValidator(CodecRegistry registry)
{
    public Job[] CreateJobs(RunRequest request)
    {
        if (request.FileIds is null || request.FileIds.Length is < 1 or > LabLimits.FilesPerRun ||
            request.Chains is null || request.Chains.Length is < 1 or > LabLimits.ChainsPerRun)
        {
            throw new ArgumentException("Выберите файлы и цепочки сжатия");
        }

        BenchmarkSettings.Validate(request.BlockSize, request.Repeats, request.Warmup);
        var chains = request.Chains.Select(NormalizeChain).ToArray();

        return request.FileIds.Distinct().SelectMany(fileId => chains.Select(chain => new Job
        {
            FileId = fileId,
            Chain = chain,
            BlockSize = request.BlockSize,
            Repeats = request.Repeats,
            Warmup = request.Warmup
        })).ToArray();
    }

    private Chain NormalizeChain(Chain chain)
    {
        if (chain is null || string.IsNullOrWhiteSpace(chain.Name) || chain.Name.Length > LabLimits.ChainNameCharacters)
        {
            throw new ArgumentException("Укажите название цепочки (до 200 символов)");
        }

        var steps = registry.NormalizeSteps(chain.Steps);
        return new(chain.Name, steps);
    }
}

namespace CompressionLab.Web.Updates;

public sealed class UpdateStartupCheck(UpdateService updates, UpdateConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.CheckOnStartup || configuration.ManifestUrl is null)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        await updates.CheckAsync(stoppingToken);
    }
}

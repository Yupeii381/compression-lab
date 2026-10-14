using System.Diagnostics;
using CompressionLab.Codecs.BuiltIn;
using CompressionLab.Core;
using CompressionLab.Web.Configuration;
using CompressionLab.Web.Endpoints;
using CompressionLab.Web.Middleware;
using CompressionLab.Web.Services;
using CompressionLab.Web.Storage;
using CompressionLab.Web.Updates;
using System.Text.Json;

namespace CompressionLab.Web.Hosting;

public static class LabApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--apply-update")
            {
                return await UpdateInstaller.RunAsync(args[1]);
            }

            var options = StartupOptions.Parse(args);
            if (options.ShowHelp)
            {
                StartupOptions.PrintHelp();
                return 0;
            }

            var registry = CreateRegistry();
            var engine = new ArchiveEngine(registry);
            if (options.RestoreSource is not null)
            {
                var header = engine.Restore(options.RestoreSource, options.RestoreDestination!, CancellationToken.None);
                Console.WriteLine($"Восстановлено: {header.Name}, {header.OriginalSize} байт; SHA-256 проверен.");
                return 0;
            }

            await RunServerAsync(options, registry, engine);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Не удалось выполнить команду: {exception.Message}");
            return 1;
        }
    }

    private static CodecRegistry CreateRegistry()
    {
        var registry = new CodecRegistry();
        registry.Register(new GzipCodec());
        registry.Register(new BrotliCodec());
        registry.Register(new DeflateCodec());
        registry.LoadPlugins(Path.Combine(AppContext.BaseDirectory, "plugins"));
        return registry;
    }

    private static async Task RunServerAsync(StartupOptions options, CodecRegistry registry, ArchiveEngine engine)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // Explorer and shortcuts may supply a working directory unrelated to the EXE.
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = "wwwroot",
            Args = []
        });

        builder.WebHost.UseUrls(options.Address);
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = LabLimits.UploadBytes);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(engine);
        builder.Services.AddSingleton(_ => new LabPaths(options.DataDirectory));
        builder.Services.AddSingleton<LabRepository>();
        builder.Services.AddSingleton<ExperimentValidator>();
        builder.Services.AddSingleton<JobQueue>();
        builder.Services.AddSingleton<FileService>();
        builder.Services.AddSingleton<ArchiveService>();
        builder.Services.AddSingleton<ExportService>();
        var configurationPath = Path.Combine(AppContext.BaseDirectory, "updates.json");
        var updateConfiguration = File.Exists(configurationPath)
            ? JsonSerializer.Deserialize<UpdateConfiguration>(File.ReadAllText(configurationPath), ArchiveEngine.Json) ?? new()
            : new UpdateConfiguration();
        if (options.UpdateFeed is not null)
        {
            updateConfiguration = updateConfiguration with { ManifestUrl = options.UpdateFeed };
        }

        if (updateConfiguration.ManifestUrl is not null)
        {
            UpdateRules.ValidateUrl(updateConfiguration.ManifestUrl);
        }

        builder.Services.AddSingleton(updateConfiguration);
        builder.Services.AddSingleton(_ => new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        })
        { Timeout = TimeSpan.FromMinutes(10) });
        builder.Services.AddSingleton<UpdateClient>();
        builder.Services.AddSingleton<UpdateService>();
        builder.Services.AddHostedService<LabWorker>();
        builder.Services.AddHostedService<UpdateStartupCheck>();

        await using var app = builder.Build();
        app.UseMiddleware<LocalRequestMiddleware>();
        app.UseMiddleware<ApiExceptionMiddleware>();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/" || context.Request.Path == StartupOptions.BasePath)
            {
                context.Response.Redirect(StartupOptions.BasePath + "/" + context.Request.QueryString);
                return;
            }

            await next(context);
        });
        app.UsePathBase(StartupOptions.BasePath);
        // Routing must see the path after the /co-lab prefix has been removed.
        app.UseRouting();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapLabEndpoints();
        app.MapFileEndpoints();
        app.MapJobEndpoints();
        app.MapExportEndpoints();
        app.MapUpdateEndpoints();

        await app.StartAsync();
        Console.WriteLine($"Compression Lab: {options.BrowserAddress}");
        Console.WriteLine($"Данные: {options.DataDirectory}");
        Console.WriteLine("Для остановки нажмите Ctrl+C.");
        if (options.OpenBrowser && !options.IsUpdateRestart)
        {
            OpenBrowser(options.BrowserAddress, app.Logger);
        }

        await app.WaitForShutdownAsync();
    }

    private static void OpenBrowser(string address, ILogger logger)
    {
        try
        {
            Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Не удалось открыть браузер. Откройте {Address} вручную.", address);
        }
    }
}

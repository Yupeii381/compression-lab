namespace CompressionLab.Web.Configuration;

public sealed record StartupOptions(
    int Port,
    string DataDirectory,
    bool OpenBrowser,
    bool ShowHelp,
    string? RestoreSource,
    string? RestoreDestination,
    string? UpdateFeed = null,
    bool IsUpdateRestart = false)
{
    public string Address => $"http://127.0.0.1:{Port}";
    public const string BasePath = "/co-lab";
    public string BrowserAddress => $"http://localhost{(Port == 80 ? "" : $":{Port}")}{BasePath}/";

    public static StartupOptions Parse(string[] args)
    {
        var port = 80;
        string? dataDirectory = null;
        var openBrowser = true;
        var showHelp = false;
        string? restoreSource = null;
        string? restoreDestination = null;
        string? updateFeed = null;
        var isUpdateRestart = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--port":
                    if (!int.TryParse(ReadValue(args, ref index), out port) || port is < 1 or > 65535)
                    {
                        throw new ArgumentException("Порт должен быть числом от 1 до 65535");
                    }
                    break;
                case "--data-dir":
                    dataDirectory = Path.GetFullPath(ReadValue(args, ref index));
                    break;
                case "--no-browser":
                    openBrowser = false;
                    break;
                case "--update-restart":
                    isUpdateRestart = true;
                    break;
                case "--update-feed":
                    updateFeed = ReadValue(args, ref index);
                    Updates.UpdateRules.ValidateUrl(updateFeed);
                    break;
                case "--help" or "-h":
                    showHelp = true;
                    break;
                case "--restore":
                    restoreSource = Path.GetFullPath(ReadValue(args, ref index));
                    restoreDestination = Path.GetFullPath(ReadValue(args, ref index));
                    break;
                default:
                    throw new ArgumentException($"Неизвестный аргумент: {args[index]}. Используйте --help.");
            }
        }

        return new(port, dataDirectory ?? DefaultDataDirectory(), openBrowser, showHelp, restoreSource, restoreDestination, updateFeed, isUpdateRestart);
    }

    public static void PrintHelp() => Console.WriteLine("""
        Compression Lab
        Запустите EXE, чтобы открыть приложение в браузере. Остановка: Ctrl+C.

        Адрес по умолчанию: http://localhost/co-lab/
        --port 8090                  Другой локальный порт (по умолчанию 80)
        --data-dir D:\CompressionData Другой каталог данных
        --no-browser                 Не открывать браузер автоматически
        --update-feed https://…       Другой источник обновлений (JSON)
        --restore input.clcs out.bin  Восстановить архив и завершить работу
        --help                       Эта справка
        """);

    private static string ReadValue(string[] args, ref int index)
    {
        var option = args[index];
        index++;
        if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Не указано значение для {option}");
        }

        return args[index];
    }

    private static string DefaultDataDirectory()
    {
        // Keep data outside replaceable release folders and read-only install directories.
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new InvalidOperationException("Не найден каталог данных пользователя. Укажите --data-dir.");
        }

        return Path.Combine(localData, "CompressionLab", "data");
    }
}

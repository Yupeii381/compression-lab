using System.Reflection;
using CompressionLab.Contracts;
using CompressionLab.Core.Plugins;

namespace CompressionLab.Core;

/// <summary>Catalog, parameter validation and discovery of independently implemented codecs.</summary>
public sealed class CodecRegistry
{
    private readonly Dictionary<string, ICompressionCodec> codecs = new(StringComparer.Ordinal);

    public List<string> Diagnostics { get; } = [];
    public IEnumerable<CodecDescriptor> Descriptors => codecs.Values.Select(codec => codec.Descriptor);

    public void Register(ICompressionCodec codec)
    {
        var descriptor = codec.Descriptor;
        if (string.IsNullOrWhiteSpace(descriptor.Id) || string.IsNullOrWhiteSpace(descriptor.Version) ||
            descriptor.Id.Length > 100 ||
            descriptor.Parameters.Select(parameter => parameter.Key).Distinct().Count() != descriptor.Parameters.Count ||
            descriptor.Parameters.Any(parameter => parameter.Min > parameter.Default || parameter.Default > parameter.Max))
        {
            throw new ArgumentException("Некорректное описание кодека");
        }

        if (!codecs.TryAdd(descriptor.Id, codec))
        {
            throw new ArgumentException($"Повтор ID кодека: {descriptor.Id}");
        }
    }

    public ICompressionCodec Get(string id)
    {
        return codecs.TryGetValue(id, out var codec)
            ? codec
            : throw new ArgumentException($"Кодек не установлен: {id}");
    }

    public Dictionary<string, int> Validate(CodecStep step)
    {
        var descriptor = Get(step.Id).Descriptor;
        if (step.Version is not null && step.Version != descriptor.Version)
        {
            throw new InvalidDataException($"Несовместимая версия {step.Id}: нужна {step.Version}, установлена {descriptor.Version}");
        }

        var options = descriptor.Parameters.ToDictionary(parameter => parameter.Key, parameter => parameter.Default);
        foreach (var (key, value) in step.Options)
        {
            var parameter = descriptor.Parameters.FirstOrDefault(parameter => parameter.Key == key)
                ?? throw new ArgumentException($"Неизвестный параметр {step.Id}.{key}");
            if (value < parameter.Min || value > parameter.Max)
            {
                throw new ArgumentException($"{key}: допустимо {parameter.Min}–{parameter.Max}");
            }

            options[key] = value;
        }

        return options;
    }

    public CodecStep[] NormalizeSteps(CodecStep[] steps)
    {
        if (steps is null || steps.Length is < 1 or > BenchmarkSettings.MaxSteps)
        {
            throw new ArgumentException("В цепочке должно быть 1–8 шагов");
        }

        return steps.Select(step =>
        {
            if (step is null || string.IsNullOrWhiteSpace(step.Id) || step.Options is null)
            {
                throw new ArgumentException("Некорректный шаг цепочки");
            }

            return new CodecStep(step.Id, Validate(step), Get(step.Id).Descriptor.Version);
        }).ToArray();
    }

    public void LoadPlugins(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var dll in Directory.EnumerateFiles(folder, "CompressionLab.Codecs.*.dll", SearchOption.AllDirectories).Order())
        {
            try
            {
                var path = Path.GetFullPath(dll);
                var assembly = new PluginLoadContext(path).LoadFromAssemblyPath(path);
                RegisterAssembly(assembly, Path.GetFileName(dll));
            }
            catch (Exception exception)
            {
                Diagnostics.Add($"{Path.GetFileName(dll)}: {exception.GetBaseException().Message}");
            }
        }
    }

    private void RegisterAssembly(Assembly assembly, string fileName)
    {
        var implementations = assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(ICompressionCodec).IsAssignableFrom(type));

        foreach (var implementation in implementations)
        {
            try
            {
                Register((ICompressionCodec)Activator.CreateInstance(implementation)!);
            }
            catch (Exception exception)
            {
                Diagnostics.Add($"{fileName} / {implementation.Name}: {exception.GetBaseException().Message}");
            }
        }
    }
}

using System.Text.Json;
using System.Linq;

public record AppSettings(
    string BaseUrl,
    string ApiKey,
    string Directory,
    string Workspace,
    string FingerprintPath,
    int MaxConcurrency,
    string[] AdditionalWorkspaces,
    Dictionary<string, string> Metadata,
    bool DryRun)
{
    public string[] AllWorkspaces
        => AdditionalWorkspaces.Length == 0
            ? new[] { Workspace }
            : new[] { Workspace }.Concat(AdditionalWorkspaces).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

internal static class SettingsLoader
{
    private const string DefaultConfigFile = "appsettings.json";

    public static AppSettings Load(string[] args)
    {
        var cli = CliArguments.Parse(args);
        var configPath = cli.GetValueOrDefault("config") ?? DefaultConfigFile;

        RawSettings? fileSettings = null;
        if (File.Exists(configPath))
        {
            var json = File.ReadAllText(configPath);
            fileSettings = JsonSerializer.Deserialize<RawSettings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }

        string baseUrl = Value("baseUrl", "ANYTHINGLLM_BASE_URL");
        string apiKey = Value("apiKey", "ANYTHINGLLM_API_KEY");
        string directory = Value("directory", "ANYTHINGLLM_DIRECTORY");
        string workspace = Value("workspace", "ANYTHINGLLM_WORKSPACE");

        var fingerprintPath = Value("fingerprintPath", "ANYTHINGLLM_FINGERPRINTS")
            ?? fileSettings?.FingerprintPath
            ?? "fingerprints.json";

        int maxConcurrency = int.TryParse(Value("maxConcurrency", "ANYTHINGLLM_MAX_CONCURRENCY"), out var parsed)
            ? Math.Max(1, parsed)
            : fileSettings?.MaxConcurrency ?? Math.Max(1, Environment.ProcessorCount);

        var additionalWorkspaces = Value("additionalWorkspaces", "ANYTHINGLLM_ADDITIONAL_WORKSPACES")
            ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? fileSettings?.AdditionalWorkspaces
            ?? Array.Empty<string>();

        var metadata = fileSettings?.Metadata ?? new Dictionary<string, string>();
        if (cli.TryGetValue("metadata", out var metadataJson) && !string.IsNullOrWhiteSpace(metadataJson))
        {
            try
            {
                var cliMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson!);
                if (cliMetadata != null)
                {
                    foreach (var kv in cliMetadata)
                        metadata[kv.Key] = kv.Value;
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Invalid JSON provided for --metadata", ex);
            }
        }

        bool dryRun = bool.TryParse(Value("dryRun", "ANYTHINGLLM_DRY_RUN"), out var dry) ? dry
            : fileSettings?.DryRun ?? false;

        return new AppSettings(
            BaseUrl: baseUrl ?? throw Missing("baseUrl"),
            ApiKey: apiKey ?? throw Missing("apiKey"),
            Directory: directory ?? throw Missing("directory"),
            Workspace: workspace ?? throw Missing("workspace"),
            FingerprintPath: fingerprintPath,
            MaxConcurrency: maxConcurrency,
            AdditionalWorkspaces: additionalWorkspaces,
            Metadata: metadata,
            DryRun: dryRun);

        string? Value(string key, string env)
        {
            if (cli.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val))
                return val;
            var envValue = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrWhiteSpace(envValue))
                return envValue;
            return fileSettings?.GetType().GetProperty(ToPropertyName(key))?.GetValue(fileSettings) as string;
        }

        static InvalidOperationException Missing(string name)
            => new($"Missing required setting '{name}'. Provide it via CLI (--{name}), environment variable, or configuration file.");
    }

    private sealed class RawSettings
    {
        public string? BaseUrl { get; set; }
        public string? ApiKey { get; set; }
        public string? Directory { get; set; }
        public string? Workspace { get; set; }
        public string? FingerprintPath { get; set; }
        public int? MaxConcurrency { get; set; }
        public string[]? AdditionalWorkspaces { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
        public bool DryRun { get; set; }
    }

    private static string ToPropertyName(string key)
        => string.Concat(key.Split('-', '_').Select(PartToPascal));

    private static string PartToPascal(string part)
        => string.IsNullOrEmpty(part)
            ? string.Empty
            : char.ToUpperInvariant(part[0]) + part.Substring(1);
}

internal sealed class CliArguments : Dictionary<string, string?>
{
    private CliArguments() : base(StringComparer.OrdinalIgnoreCase) { }

    public static CliArguments Parse(string[] args)
    {
        var result = new CliArguments();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--"))
                continue;

            arg = arg[2..];
            string? value = null;

            var eqIndex = arg.IndexOf('=');
            if (eqIndex >= 0)
            {
                value = arg[(eqIndex + 1)..];
                arg = arg[..eqIndex];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                value = args[++i];
            }

            result[arg] = value;
        }

        return result;
    }

    public string? GetValueOrDefault(string key)
        => TryGetValue(key, out var value) ? value : null;
}

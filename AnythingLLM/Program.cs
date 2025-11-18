using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public class AnythingLLMClient
{
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly HttpClient _client;

    public AnythingLLMClient(string baseUrl, string apiKey)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;

        _client = new HttpClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    public async Task<bool> AuthenticateAsync()
    {
        var url = $"{_baseUrl}/api/v1/auth";
        var response = await _client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
            return true;

        throw new Exception($"Falha na autenticação: {response.StatusCode} - {body}");
    }

    public async Task<string> UploadFileAsync(
        string filePath,
        string addToWorkspaces = "",
        string metadataJson = "{}",
        string folder = "")
    {
        var url = $"{_baseUrl}/api/v1/document/upload";

        if (!string.IsNullOrEmpty(folder))
            url += $"/{folder}";

        using var form = new MultipartFormDataContent();
        using var fileStream = File.OpenRead(filePath);

        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        form.Add(fileContent, "file", Path.GetFileName(filePath));

        if (!string.IsNullOrWhiteSpace(addToWorkspaces))
            form.Add(new StringContent(addToWorkspaces), "addToWorkspaces");

        form.Add(new StringContent(metadataJson), "metadata");

        var response = await _client.PostAsync(url, form);
        var responseText = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Erro no upload: {response.StatusCode} - {responseText}");

        return responseText;
    }
}

public class FingerprintStore
{
    private readonly string _jsonPath;
    private readonly Dictionary<string, string> _fingerprints = new();
    private readonly object _lock = new();

    public FingerprintStore(string jsonPath)
    {
        _jsonPath = jsonPath;

        if (File.Exists(jsonPath))
        {
            try
            {
                var json = File.ReadAllText(jsonPath);
                var des = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (des != null)
                {
                    foreach (var kv in des)
                        _fingerprints[kv.Key] = kv.Value;
                }
            }
            catch
            {
                // If deserialization fails, start with empty store.
            }
        }
    }

    public string ComputeFingerprint(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);

        var hash = sha.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    public bool HasChanged(string filePath, string newFingerprint)
    {
        lock (_lock)
        {
            if (!_fingerprints.TryGetValue(filePath, out var oldFp))
                return true;

            return newFingerprint != oldFp;
        }
    }

    public void SetFingerprint(string filePath, string fingerprint)
    {
        lock (_lock)
        {
            _fingerprints[filePath] = fingerprint;
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            var json = JsonSerializer.Serialize(_fingerprints, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_jsonPath, json);
        }
    }
}

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        AppSettings settings;
        try
        {
            settings = SettingsLoader.Load(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"⚠️ Failed to load configuration: {ex.Message}");
            return 1;
        }

        if (!Directory.Exists(settings.Directory))
        {
            Console.Error.WriteLine($"❌ Directory '{settings.Directory}' not found.");
            return 1;
        }

        Console.WriteLine($"📁 Monitoring: {settings.Directory}");
        Console.WriteLine($"🧠 Workspace: {settings.Workspace}");
        if (settings.AllWorkspaces.Length > 1)
            Console.WriteLine($" ➕ Additional workspaces: {string.Join(", ", settings.AllWorkspaces.Skip(1))}");
        Console.WriteLine($"🗂️  Fingerprints file: {settings.FingerprintPath}");
        if (settings.DryRun)
            Console.WriteLine("🚫 DRY RUN — uploads will be skipped.");

        var client = new AnythingLLMClient(settings.BaseUrl, settings.ApiKey);

        Console.WriteLine("A autenticar...");
        await client.AuthenticateAsync();
        Console.WriteLine("Autenticado com sucesso!");

        var fpStore = new FingerprintStore(settings.FingerprintPath);
        using var semaphore = new SemaphoreSlim(settings.MaxConcurrency);

        var files = Directory.GetFiles(settings.Directory, "*", SearchOption.AllDirectories);
        var tasks = new List<Task>();
        var failures = new List<(string file, Exception ex)>();
        int totalFilesToUpload = 0;
        int completedUploads = 0;

        var workspaceList = string.Join(',', settings.AllWorkspaces);
        var metadataJson = settings.Metadata.Count == 0 ? "{}" : JsonSerializer.Serialize(settings.Metadata);

        foreach (var file in files)
        {
            Console.WriteLine($"\n📄 Checking: {file}");

            string fingerprint;
            try
            {
                fingerprint = fpStore.ComputeFingerprint(file);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Failed to compute fingerprint for {file}: {ex.Message}");
                failures.Add((file, ex));
                continue;
            }

            if (!fpStore.HasChanged(file, fingerprint))
            {
                Console.WriteLine("➡️  No changes — skip\n");
                continue;
            }

            var relative = Path.GetRelativePath(settings.Directory, file).Replace("\\", "/");
            var relativeDir = Path.GetDirectoryName(relative)?.Replace("\\", "/") ?? string.Empty;
            string folder = string.IsNullOrEmpty(relativeDir) ? settings.Workspace : $"{settings.Workspace}/{relativeDir}";

            Console.WriteLine("🔄 Changed — scheduling upload...");
            Interlocked.Increment(ref totalFilesToUpload);

            var task = Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    Console.WriteLine($"⤴️ Uploading {file} to {folder} ...");

                    if (!settings.DryRun)
                    {
                        await client.UploadFileAsync(
                            filePath: file,
                            addToWorkspaces: workspaceList,
                            metadataJson: metadataJson,
                            folder: folder
                        );

                        Console.WriteLine($"✔️ Upload completed: {file}");
                        fpStore.SetFingerprint(file, fingerprint);
                    }
                    else
                    {
                        Console.WriteLine("(dry run) Upload skipped");
                    }

                    int done = Interlocked.Increment(ref completedUploads);
                    Console.WriteLine($"📤 Progresso geral: {done}/{totalFilesToUpload} ficheiros ({(done * 100.0 / Math.Max(1, totalFilesToUpload)):F1}%)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Upload failed for {file}: {ex.Message}");
                    lock (failures)
                    {
                        failures.Add((file, ex));
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            tasks.Add(task);
        }

        await Task.WhenAll(tasks);

        if (!settings.DryRun)
        {
            try
            {
                fpStore.Save();
                Console.WriteLine("\n📦 Fingerprints saved.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Failed to save fingerprints: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("\n(Dry run) Fingerprints were not persisted.");
        }

        Console.WriteLine($"\nDone. Processed {files.Length} files. Uploaded: {completedUploads}, Failed: {failures.Count}.");

        if (failures.Count > 0)
        {
            Console.WriteLine("Failures:");
            foreach (var f in failures)
                Console.WriteLine($"- {f.file}: {f.ex.Message}");
        }

        return failures.Count == 0 ? 0 : 2;
    }
}

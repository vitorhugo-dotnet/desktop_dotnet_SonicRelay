using System.Text.Json;

namespace SonicRelay.Windows.Core.Configuration;

public sealed class UserConfigurationLoader
{
    private const string ProductionBackendUrl = "https://sonicrelay-api.hugodotnet.dev/";
    private const string ProductionSignalingUrl = "wss://sonicrelay-api.hugodotnet.dev/ws/signaling";
    private const string LegacyBackendUrl = "https://localhost:5001/";
    private const string LegacySignalingUrl = "wss://localhost:5001/ws/signaling";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SonicRelay",
        "WindowsPublisher");

    public static string DefaultPath => Path.Combine(DefaultDirectory, "appsettings.json");

    private readonly string _path;

    public UserConfigurationLoader(string? path = null) => _path = path ?? DefaultPath;

    public async Task<PublisherConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var template = new ConfigurationDocument(
                ProductionBackendUrl,
                ProductionSignalingUrl,
                4,
                false);
            await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(template, JsonOptions), cancellationToken);
        }

        try
        {
            ConfigurationDocument document;
            await using (var stream = File.OpenRead(_path))
            {
                document = await JsonSerializer.DeserializeAsync<ConfigurationDocument>(stream, JsonOptions, cancellationToken)
                    ?? throw new ConfigurationValidationException("Configuration file is empty.");
            }

            if (IsLegacyDefaultConfiguration(document))
            {
                document = document with
                {
                    BackendBaseUrl = ProductionBackendUrl,
                    SignalingBaseUrl = ProductionSignalingUrl
                };
                await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(document, JsonOptions), cancellationToken);
            }

            var configuration = new PublisherConfiguration(
                ParseUri(document.BackendBaseUrl, "BackendBaseUrl"),
                ParseUri(document.SignalingBaseUrl, "SignalingBaseUrl"),
                document.DefaultMaxViewers,
                document.DevelopmentMode);
            configuration.Validate();
            return configuration;
        }
        catch (ConfigurationValidationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ConfigurationValidationException("Configuration file contains invalid JSON.", exception);
        }
    }

    /// <summary>
    /// Persists <paramref name="backendBaseUrl"/> (with its derived signaling
    /// URL) as the configured backend, preserving the other settings already
    /// in the file. Without this, the template written on first run keeps
    /// pointing every startup at localhost, so the stored session is never
    /// restored against the backend the user actually signed in to.
    /// </summary>
    public async Task SaveBackendAsync(Uri backendBaseUrl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backendBaseUrl);
        var normalized = backendBaseUrl.AbsoluteUri.EndsWith('/')
            ? backendBaseUrl
            : new Uri(backendBaseUrl.AbsoluteUri + "/");
        var signaling = new UriBuilder(new Uri(normalized, "ws/signaling"))
        {
            Scheme = string.Equals(normalized.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? "wss" : "ws"
        };

        var existing = await TryReadDocumentAsync(cancellationToken);
        var document = new ConfigurationDocument(
            normalized.AbsoluteUri,
            signaling.Uri.AbsoluteUri,
            existing?.DefaultMaxViewers is > 0 ? existing.DefaultMaxViewers : 4,
            existing?.DevelopmentMode ?? false);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(document, JsonOptions), cancellationToken);
    }

    private async Task<ConfigurationDocument?> TryReadDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return null;
        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<ConfigurationDocument>(stream, JsonOptions, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Uri ParseUri(string? value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new ConfigurationValidationException($"{name} must be an absolute URL.");
        }

        return uri;
    }

    private static bool IsLegacyDefaultConfiguration(ConfigurationDocument document) =>
        !document.DevelopmentMode &&
        string.Equals(document.BackendBaseUrl, LegacyBackendUrl, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(document.SignalingBaseUrl, LegacySignalingUrl, StringComparison.OrdinalIgnoreCase);

    private sealed record ConfigurationDocument(
        string? BackendBaseUrl,
        string? SignalingBaseUrl,
        int DefaultMaxViewers,
        bool DevelopmentMode);
}

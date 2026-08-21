using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;

namespace RDM.UI.Services;

/// <summary>
/// Reads and writes the per-machine sections of rdm.config.json that are
/// edited through the Settings window: general, audio (buffers),
/// stream_titles, and the api.base_url port.
/// </summary>
public sealed class SettingsConfigService
{
    private static readonly string ConfigFile = ConfigPaths.FilePath;
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented    = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private readonly ILogger<SettingsConfigService> _logger;

    public SettingsConfigService(ILogger<SettingsConfigService> logger) => _logger = logger;

    public async Task<JsonObject> LoadAsync()
    {
        if (!File.Exists(ConfigFile)) return new JsonObject();
        try
        {
            var json = await File.ReadAllTextAsync(ConfigFile);
            return (JsonNode.Parse(json) as JsonObject) ?? new JsonObject();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read {File}", ConfigFile);
            return new JsonObject();
        }
    }

    public async Task SaveAsync(JsonObject root)
    {
        try
        {
            await File.WriteAllTextAsync(ConfigFile, root.ToJsonString(WriteOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write {File}", ConfigFile);
            throw;
        }
    }
}

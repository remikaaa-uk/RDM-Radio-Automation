namespace RDM.Core.Models;

/// <summary>
/// Configuration for ImportPipeline. Registered as singleton at the composition root.
/// Values sourced from rdm.config.json via IConfiguration — never hardcoded.
/// </summary>
public sealed record ImportPipelineSettings(string ImageCachePath);

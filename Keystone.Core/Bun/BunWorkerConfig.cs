using System.Text.Json.Serialization;

namespace Keystone.Core;

public class BunWorkerConfig
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("servicesDir")]
    public string ServicesDir { get; init; } = "";

    [JsonPropertyName("autoStart")]
    public bool AutoStart { get; init; } = true;

    [JsonPropertyName("browserAccess")]
    public bool BrowserAccess { get; init; } = false;

    [JsonPropertyName("isExtensionHost")]
    public bool IsExtensionHost { get; init; } = false;

    [JsonPropertyName("maxRestarts")]
    public int MaxRestarts { get; init; } = 5;

    [JsonPropertyName("baseBackoffMs")]
    public int BaseBackoffMs { get; init; } = 1000;

    [JsonPropertyName("allowedChannels")]
    public List<string>? AllowedChannels { get; init; }
}

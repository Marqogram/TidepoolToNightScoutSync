using Newtonsoft.Json;

namespace TidepoolToNightScoutSync.Core.Model.Nightscout;

public class Settings
{
    [JsonProperty("units")] public string? Units { get; set; }
}
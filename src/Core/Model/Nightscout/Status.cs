using Newtonsoft.Json;

namespace TidepoolToNightScoutSync.Core.Model.Nightscout;

public class Status
{
    [JsonProperty("settings")] public Settings? Settings { get; set; }
}
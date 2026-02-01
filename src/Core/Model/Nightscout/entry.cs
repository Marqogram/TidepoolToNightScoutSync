using System;

namespace TidepoolToNightScoutSync.Core.Model.Nightscout;

public class Entry
{
    public string Type { get; set; } = "sgv";
    public int Sgv { get; set; }
    public long Date { get; set; }
    public string DateString { get; set; } = "";
    public string Device { get; set; } = "Tidepool";
    public string Direction { get; set; } = "Flat";
    public int Noise { get; set; } = 1;
}

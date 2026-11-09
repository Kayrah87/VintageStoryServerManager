namespace VintageStoryServerManager.Core.Configuration;

public sealed class ModDbOptions
{
    public const string SectionName = "ModDb";

    /// <summary>Base URL of the Vintage Story mod database.</summary>
    public string BaseUrl { get; set; } = "https://mods.vintagestory.at/";

    /// <summary>Base URL of the game version API (stable.json / unstable.json).</summary>
    public string GameApiUrl { get; set; } = "https://api.vintagestory.at/";
}

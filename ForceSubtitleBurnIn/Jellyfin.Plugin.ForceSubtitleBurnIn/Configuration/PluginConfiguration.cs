using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ForceSubtitleBurnIn.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the override is applied.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

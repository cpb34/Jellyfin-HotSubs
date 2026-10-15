using System;
using Jellyfin.Plugin.ForceSubtitleBurnIn.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ForceSubtitleBurnIn;

/// <summary>
/// Forces SubtitleMethod=Encode on transcoded video stream requests.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>
{
    /// <summary>
    /// Plugin id; must match "guid" in meta.json.
    /// </summary>
    public static readonly Guid PluginId = new("87379d04-121f-4b92-9c79-6741d9205946");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
    }

    /// <inheritdoc />
    public override string Name => "Force Subtitle Burn-In";

    /// <inheritdoc />
    public override Guid Id => PluginId;
}

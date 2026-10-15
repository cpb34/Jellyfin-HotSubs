using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HotSubs;

/// <summary>
/// Forces server-side subtitle burn-in.
/// <list type="bullet">
/// <item>POST /Items/{id}/PlaybackInfo: the posted DeviceProfile is made to accept no client-side subtitle delivery
/// (no SubtitleProfiles, no subtitles in HLS manifests), and direct play/stream are disabled for items that have
/// subtitle streams. The server then answers with a transcode URL carrying SubtitleMethod=Encode.</item>
/// <item>Video stream actions: a non-static request that selects a subtitle gets subtitleMethod=Encode.</item>
/// </list>
/// Only action arguments (and objects bound for this request) are changed. Never sets a result,
/// never short-circuits, never catches.
/// </summary>
public sealed class HotSubsActionFilter : IActionFilter
{
    // Jellyfin.Api is not published to NuGet, so controllers are matched by type name + assembly name.
    private const string ApiAssemblyName = "Jellyfin.Api";

    private const string PlaybackInfoDtoTypeName = "Jellyfin.Api.Models.MediaInfoDtos.PlaybackInfoDto";

    // Stream action argument names ("@static" binds as "static").
    private const string SubtitleMethodArg = "subtitleMethod";
    private const string SubtitleStreamIndexArg = "subtitleStreamIndex";
    private const string StaticArg = "static";

    // PlaybackInfo action argument names.
    private const string ItemIdArg = "itemId";
    private const string MediaSourceIdArg = "mediaSourceId";
    private const string EnableDirectPlayArg = "enableDirectPlay";
    private const string EnableDirectStreamArg = "enableDirectStream";
    private const string PlaybackInfoDtoArg = "playbackInfoDto";

    // PlaybackInfoDto property names.
    private const string DeviceProfileProperty = "DeviceProfile";
    private const string MediaSourceIdProperty = "MediaSourceId";

    private static readonly FrozenSet<string> StreamTargets = FrozenSet.ToFrozenSet(
        [
            "Jellyfin.Api.Controllers.DynamicHlsController.GetMasterHlsVideoPlaylist",
            "Jellyfin.Api.Controllers.DynamicHlsController.GetVariantHlsVideoPlaylist",
            "Jellyfin.Api.Controllers.DynamicHlsController.GetHlsVideoSegment",
            "Jellyfin.Api.Controllers.DynamicHlsController.GetLiveHlsStream",
            "Jellyfin.Api.Controllers.VideosController.GetVideoStream",
            "Jellyfin.Api.Controllers.VideosController.GetVideoStreamByContainer",
        ],
        StringComparer.Ordinal);

    private const string PlaybackInfoTarget = "Jellyfin.Api.Controllers.MediaInfoController.GetPostedPlaybackInfo";

    // Expected declared parameter types; a mismatch means the server API drifted.
    private static readonly (string Name, Type Type)[] StreamParameters =
    [
        (SubtitleMethodArg, typeof(SubtitleDeliveryMethod?)),
        (SubtitleStreamIndexArg, typeof(int?)),
        (StaticArg, typeof(bool?)),
    ];

    private static readonly (string Name, Type Type)[] PlaybackInfoParameters =
    [
        (ItemIdArg, typeof(Guid)),
        (MediaSourceIdArg, typeof(string)),
        (EnableDirectPlayArg, typeof(bool?)),
        (EnableDirectStreamArg, typeof(bool?)),
    ];

    private readonly IPluginManager _pluginManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IModelMetadataProvider _metadataProvider;
    private readonly ILogger<HotSubsActionFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HotSubsActionFilter"/> class.
    /// </summary>
    /// <param name="pluginManager">Plugin manager, used to read this plugin's configuration.</param>
    /// <param name="mediaSourceManager">Media source manager, used to check for subtitle streams.</param>
    /// <param name="metadataProvider">MVC model metadata, used to read/write public DTO properties of Jellyfin.Api types.</param>
    /// <param name="logger">Logger.</param>
    public HotSubsActionFilter(
        IPluginManager pluginManager,
        IMediaSourceManager mediaSourceManager,
        IModelMetadataProvider metadataProvider,
        ILogger<HotSubsActionFilter> logger)
    {
        _pluginManager = pluginManager;
        _mediaSourceManager = mediaSourceManager;
        _metadataProvider = metadataProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor descriptor
            || !string.Equals(descriptor.ControllerTypeInfo.Assembly.GetName().Name, ApiAssemblyName, StringComparison.Ordinal))
        {
            return;
        }

        var key = descriptor.ControllerTypeInfo.FullName + "." + descriptor.ActionName;
        var isStream = StreamTargets.Contains(key);
        var isPlaybackInfo = !isStream && string.Equals(key, PlaybackInfoTarget, StringComparison.Ordinal);
        if (!isStream && !isPlaybackInfo)
        {
            return;
        }

        if (!IsEnabled())
        {
            return;
        }

        var drift = FindSignatureDrift(descriptor, isStream ? StreamParameters : PlaybackInfoParameters, isPlaybackInfo);
        if (drift is not null)
        {
            _logger.LogWarning(
                "HotSubs: {Controller}.{Action} signature changed ({Drift}); passing through unchanged",
                descriptor.ControllerName,
                descriptor.ActionName,
                drift);
            return;
        }

        if (isStream)
        {
            ApplyToStream(context, descriptor);
        }
        else
        {
            ApplyToPlaybackInfo(context, descriptor);
        }
    }

    /// <inheritdoc />
    public void OnActionExecuted(ActionExecutedContext context)
    {
    }

    private static string? FindSignatureDrift(ControllerActionDescriptor descriptor, (string Name, Type Type)[] expected, bool requireDto)
    {
        List<string>? problems = null;
        foreach (var (name, expectedType) in expected)
        {
            var actualType = FindParameterType(descriptor, name);
            if (actualType != expectedType)
            {
                (problems ??= []).Add(actualType is null
                    ? $"{name}: missing"
                    : $"{name}: {actualType} (expected {expectedType})");
            }
        }

        if (requireDto)
        {
            var dtoType = FindParameterType(descriptor, PlaybackInfoDtoArg);
            if (!string.Equals(dtoType?.FullName, PlaybackInfoDtoTypeName, StringComparison.Ordinal))
            {
                (problems ??= []).Add($"{PlaybackInfoDtoArg}: {dtoType?.FullName ?? "missing"} (expected {PlaybackInfoDtoTypeName})");
            }
        }

        return problems is null ? null : string.Join("; ", problems);
    }

    private static Type? FindParameterType(ControllerActionDescriptor descriptor, string name)
    {
        foreach (var parameter in descriptor.Parameters)
        {
            if (string.Equals(parameter.Name, name, StringComparison.Ordinal))
            {
                return parameter.ParameterType;
            }
        }

        return null;
    }

    private void ApplyToStream(ActionExecutingContext context, ControllerActionDescriptor descriptor)
    {
        var args = context.ActionArguments;

        // Unbound optional parameters are absent from ActionArguments; absent index == no subtitle selected.
        if (!args.TryGetValue(SubtitleStreamIndexArg, out var indexObj) || indexObj is null)
        {
            return;
        }

        if (indexObj is not int index)
        {
            WarnType(descriptor, SubtitleStreamIndexArg, indexObj);
            return;
        }

        if (index < 0)
        {
            return;
        }

        if (args.TryGetValue(StaticArg, out var staticObj) && staticObj is not null)
        {
            if (staticObj is not bool isStatic)
            {
                WarnType(descriptor, StaticArg, staticObj);
                return;
            }

            if (isStatic)
            {
                return;
            }
        }

        SubtitleDeliveryMethod? requested = null;
        if (args.TryGetValue(SubtitleMethodArg, out var methodObj) && methodObj is not null)
        {
            if (methodObj is not SubtitleDeliveryMethod method)
            {
                WarnType(descriptor, SubtitleMethodArg, methodObj);
                return;
            }

            if (method == SubtitleDeliveryMethod.Encode)
            {
                return;
            }

            requested = method;
        }

        // Adding the key is honored: the invoker fills action parameters from ActionArguments by name.
        args[SubtitleMethodArg] = SubtitleDeliveryMethod.Encode;

        _logger.LogDebug(
            "HotSubs: {Controller}.{Action} subtitleStreamIndex={Index} subtitleMethod {Requested} -> Encode",
            descriptor.ControllerName,
            descriptor.ActionName,
            index,
            requested?.ToString() ?? "(unset)");
    }

    private void ApplyToPlaybackInfo(ActionExecutingContext context, ControllerActionDescriptor descriptor)
    {
        var args = context.ActionArguments;

        if (!args.TryGetValue(PlaybackInfoDtoArg, out var dto) || dto is null)
        {
            // Empty body: the server falls back to the device's registered profile, a shared object this
            // plugin must not mutate.
            _logger.LogDebug("HotSubs: PlaybackInfo without a body; passing through unchanged");
            return;
        }

        var dtoMetadata = _metadataProvider.GetMetadataForType(dto.GetType());
        var profileMetadata = dtoMetadata.Properties[DeviceProfileProperty];
        if (profileMetadata?.PropertyGetter is null || profileMetadata.ModelType != typeof(DeviceProfile))
        {
            _logger.LogWarning(
                "HotSubs: {Type}.{Property} missing or not a DeviceProfile; passing through unchanged",
                dto.GetType(),
                DeviceProfileProperty);
            return;
        }

        if (profileMetadata.PropertyGetter(dto) is not DeviceProfile profile)
        {
            _logger.LogDebug("HotSubs: PlaybackInfo without a DeviceProfile; passing through unchanged");
            return;
        }

        // No External/Embed/Hls profile left: StreamBuilder's fallback for every subtitle is Encode
        // (StreamBuilder.GetSubtitleProfile), which also rules out direct play for a selected subtitle.
        profile.SubtitleProfiles = [];

        // Keep HLS master playlists free of client-renderable subtitle renditions.
        foreach (var transcodingProfile in profile.TranscodingProfiles ?? [])
        {
            transcodingProfile.EnableSubtitlesInManifest = false;
        }

        var itemId = args.TryGetValue(ItemIdArg, out var itemIdObj) && itemIdObj is Guid g ? g : Guid.Empty;
        var mediaSourceId = args.TryGetValue(MediaSourceIdArg, out var msObj) && msObj is string ms
            ? ms
            : dtoMetadata.Properties[MediaSourceIdProperty]?.PropertyGetter?.Invoke(dto) as string;

        var hasSubtitles = HasSubtitleStreams(itemId)
            || (Guid.TryParse(mediaSourceId, out var sourceId) && sourceId != itemId && HasSubtitleStreams(sourceId));

        if (hasSubtitles)
        {
            // Query values take precedence over the body in GetPostedPlaybackInfo, so these win.
            // Without this, a session that starts with subtitles off direct-plays, and turning a
            // subtitle on mid-playback switches tracks in the client without asking the server.
            args[EnableDirectPlayArg] = false;
            args[EnableDirectStreamArg] = false;
        }

        _logger.LogDebug(
            "HotSubs: PlaybackInfo item {ItemId}: subtitle profiles cleared, manifest subtitles off, direct play {DirectPlay}",
            itemId,
            hasSubtitles ? "disabled (item has subtitle streams)" : "unchanged (no subtitle streams)");
    }

    private bool HasSubtitleStreams(Guid itemId)
    {
        return !itemId.Equals(Guid.Empty)
            && _mediaSourceManager.GetMediaStreams(new MediaStreamQuery { ItemId = itemId, Type = MediaStreamType.Subtitle }).Count > 0;
    }

    private bool IsEnabled()
    {
        // Read through IPluginManager rather than a static Instance field; default (true) if not resolvable.
        var config = (_pluginManager.GetPlugin(Plugin.PluginId)?.Instance as Plugin)?.Configuration;
        return config?.Enabled ?? true;
    }

    private void WarnType(ControllerActionDescriptor descriptor, string name, object value)
    {
        _logger.LogWarning(
            "HotSubs: {Controller}.{Action} argument {Argument} has unexpected type {Type}; passing through unchanged",
            descriptor.ControllerName,
            descriptor.ActionName,
            name,
            value.GetType());
    }
}

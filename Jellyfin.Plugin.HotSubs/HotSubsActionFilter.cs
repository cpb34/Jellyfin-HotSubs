using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Dlna;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HotSubs;

/// <summary>
/// Forces server-side subtitle burn-in.
/// <list type="bullet">
/// <item>POST /Items/{id}/PlaybackInfo: the posted DeviceProfile is made to accept no client-side subtitle delivery
/// (no SubtitleProfiles, no subtitles in HLS manifests), and direct play, direct stream and video stream copy are
/// disabled for every item. The server then always encodes the video, with a selected subtitle burned in
/// (SubtitleMethod=Encode).</item>
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
    private const string EnableDirectPlayArg = "enableDirectPlay";
    private const string EnableDirectStreamArg = "enableDirectStream";
    private const string AllowVideoStreamCopyArg = "allowVideoStreamCopy";
    private const string PlaybackInfoDtoArg = "playbackInfoDto";

    // PlaybackInfoDto property names.
    private const string DeviceProfileProperty = "DeviceProfile";

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
        (EnableDirectPlayArg, typeof(bool?)),
        (EnableDirectStreamArg, typeof(bool?)),
        (AllowVideoStreamCopyArg, typeof(bool?)),
    ];

    private readonly IPluginManager _pluginManager;
    private readonly IModelMetadataProvider _metadataProvider;
    private readonly ILogger<HotSubsActionFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HotSubsActionFilter"/> class.
    /// </summary>
    /// <param name="pluginManager">Plugin manager, used to read this plugin's configuration.</param>
    /// <param name="metadataProvider">MVC model metadata, used to read/write public DTO properties of Jellyfin.Api types.</param>
    /// <param name="logger">Logger.</param>
    public HotSubsActionFilter(
        IPluginManager pluginManager,
        IModelMetadataProvider metadataProvider,
        ILogger<HotSubsActionFilter> logger)
    {
        _pluginManager = pluginManager;
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

        // Query values take precedence over the body in GetPostedPlaybackInfo, so these win, with or
        // without a body. No direct play and no direct stream keep the server in the path, so a subtitle
        // turned on mid-playback cannot be switched in the client. No video copy: the video is always
        // encoded, never remuxed. The server carries allowVideoStreamCopy=false into the TranscodingUrl.
        args[EnableDirectPlayArg] = false;
        args[EnableDirectStreamArg] = false;
        args[AllowVideoStreamCopyArg] = false;

        if (!args.TryGetValue(PlaybackInfoDtoArg, out var dto) || dto is null)
        {
            // Empty body: the server falls back to the device's registered profile, a shared object this
            // plugin must not mutate.
            _logger.LogDebug("HotSubs: PlaybackInfo without a body; transcode forced, subtitle profiles unchanged");
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

        _logger.LogDebug("HotSubs: PlaybackInfo: transcode forced, subtitle profiles cleared, manifest subtitles off");
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

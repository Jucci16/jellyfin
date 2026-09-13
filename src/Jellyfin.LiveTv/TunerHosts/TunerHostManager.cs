using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.LiveTv.Configuration;
using Jellyfin.LiveTv.Guide;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.TunerHosts;

/// <inheritdoc />
public class TunerHostManager : ITunerHostManager
{
    private const int TunerDiscoveryDurationMs = 3000;

    private readonly ILogger<TunerHostManager> _logger;
    private readonly IConfigurationManager _config;
    private readonly ITaskManager _taskManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ITunerHost[] _tunerHosts;

    /// <summary>
    /// Initializes a new instance of the <see cref="TunerHostManager"/> class.
    /// </summary>
    /// <param name="logger">The <see cref="ILogger{T}"/>.</param>
    /// <param name="config">The <see cref="IConfigurationManager"/>.</param>
    /// <param name="taskManager">The <see cref="ITaskManager"/>.</param>
    /// <param name="libraryManager">The <see cref="ILibraryManager"/>.</param>
    /// <param name="tunerHosts">The <see cref="IEnumerable{T}"/>.</param>
    public TunerHostManager(
        ILogger<TunerHostManager> logger,
        IConfigurationManager config,
        ITaskManager taskManager,
        ILibraryManager libraryManager,
        IEnumerable<ITunerHost> tunerHosts)
    {
        _logger = logger;
        _config = config;
        _taskManager = taskManager;
        _libraryManager = libraryManager;
        _tunerHosts = tunerHosts.Where(t => t.IsSupported).ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<ITunerHost> TunerHosts => _tunerHosts;

    /// <inheritdoc />
    public IEnumerable<NameIdPair> GetTunerHostTypes()
        => _tunerHosts.OrderBy(i => i.Name).Select(i => new NameIdPair
        {
            Name = i.Name,
            Id = i.Type
        });

    /// <inheritdoc />
    public IReadOnlyList<TunerHostInfo> GetConfiguredTunerHosts()
        => _config.GetLiveTvConfiguration().TunerHosts;

    /// <inheritdoc />
    public async Task<TunerHostInfo> SaveTunerHost(TunerHostInfo info, bool dataSourceChanged = true)
    {
        info = JsonSerializer.Deserialize<TunerHostInfo>(JsonSerializer.SerializeToUtf8Bytes(info))!;

        var provider = _tunerHosts.FirstOrDefault(i => string.Equals(info.Type, i.Type, StringComparison.OrdinalIgnoreCase));

        if (provider is null)
        {
            throw new ResourceNotFoundException();
        }

        if (provider is IConfigurableTunerHost configurable)
        {
            await configurable.Validate(info).ConfigureAwait(false);
        }

        var config = _config.GetLiveTvConfiguration();

        var list = config.TunerHosts;
        var index = Array.FindIndex(list, i => string.Equals(i.Id, info.Id, StringComparison.OrdinalIgnoreCase));

        if (index == -1 || string.IsNullOrWhiteSpace(info.Id))
        {
            info.Id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            config.TunerHosts = [.. list, info];
        }
        else
        {
            config.TunerHosts[index] = info;
        }

        _config.SaveConfiguration("livetv", config);

        if (dataSourceChanged)
        {
            _taskManager.CancelIfRunningAndQueue<RefreshGuideScheduledTask>();
        }

        return info;
    }

    /// <inheritdoc />
    public void DeleteTunerHost(string? id)
    {
        var config = _config.GetLiveTvConfiguration();
        config.TunerHosts = config.TunerHosts.Where(i => !string.Equals(id, i.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
        _config.SaveConfiguration("livetv", config);

        // Clean up the disk cache file for this tuner.
        // Tuner IDs are generated as Guid.NewGuid().ToString("N")
        // reject anything else so we never use untrusted input in a path or log entry
        if (Guid.TryParseExact(id, "N", out var tunerGuid))
        {
            var safeId = tunerGuid.ToString("N", CultureInfo.InvariantCulture);
            var channelCacheFile = Path.Combine(_config.CommonApplicationPaths.CachePath, safeId + "_channels");
            try
            {
                File.Delete(channelCacheFile);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Error deleting channel cache file for tuner {TunerId}", safeId);
            }
        }

        _taskManager.CancelIfRunningAndQueue<RefreshGuideScheduledTask>();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TunerHostInfo> DiscoverTuners(bool newDevicesOnly)
    {
        var configuredDeviceIds = _config.GetLiveTvConfiguration().TunerHosts
            .Where(i => !string.IsNullOrWhiteSpace(i.DeviceId))
            .Select(i => i.DeviceId)
            .ToList();

        foreach (var host in _tunerHosts)
        {
            var discoveredDevices = await DiscoverDevices(host, TunerDiscoveryDurationMs, CancellationToken.None).ConfigureAwait(false);
            foreach (var tuner in discoveredDevices)
            {
                if (!newDevicesOnly || !configuredDeviceIds.Contains(tuner.DeviceId, StringComparer.OrdinalIgnoreCase))
                {
                    yield return tuner;
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task ScanForTunerDeviceChanges(CancellationToken cancellationToken)
    {
        foreach (var host in _tunerHosts)
        {
            await ScanForTunerDeviceChanges(host, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ScanForTunerDeviceChanges(ITunerHost host, CancellationToken cancellationToken)
    {
        var discoveredDevices = await DiscoverDevices(host, TunerDiscoveryDurationMs, cancellationToken).ConfigureAwait(false);

        var configuredDevices = _config.GetLiveTvConfiguration().TunerHosts
            .Where(i => string.Equals(i.Type, host.Type, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var device in discoveredDevices)
        {
            var configuredDevice = configuredDevices.FirstOrDefault(i => string.Equals(i.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase));

            if (configuredDevice is not null && !string.Equals(device.Url, configuredDevice.Url, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Tuner url has changed from {PreviousUrl} to {NewUrl}", configuredDevice.Url, device.Url);

                configuredDevice.Url = device.Url;
                await SaveTunerHost(configuredDevice).ConfigureAwait(false);
            }
        }
    }

    private async Task<IList<TunerHostInfo>> DiscoverDevices(ITunerHost host, int discoveryDurationMs, CancellationToken cancellationToken)
    {
        try
        {
            var discoveredDevices = await host.DiscoverDevices(discoveryDurationMs, cancellationToken).ConfigureAwait(false);

            foreach (var device in discoveredDevices)
            {
                _logger.LogInformation("Discovered tuner device {0} at {1}", host.Name, device.Url);
            }

            return discoveredDevices;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error discovering tuner devices");

            return Array.Empty<TunerHostInfo>();
        }
    }

    /// <inheritdoc />
    public async Task<HashSet<Guid>?> GetAllowedChannelItemIds(User user, CancellationToken cancellationToken)
    {
        var allowedTunerHostIds = GetAllowedTunerHostIds(user);
        if (allowedTunerHostIds is null)
        {
            return null;
        }

        var channelTunerHostIds = await GetChannelTunerHostIds(true, cancellationToken).ConfigureAwait(false);

        var allChannels = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.LiveTvChannel]
        });

        var result = new HashSet<Guid>();
        foreach (var channel in allChannels)
        {
            if (channel is LiveTvChannel liveTvChannel
                && !string.IsNullOrEmpty(liveTvChannel.ExternalId)
                && channelTunerHostIds.TryGetValue(liveTvChannel.ExternalId, out var tunerIds)
                && tunerIds.Overlaps(allowedTunerHostIds))
            {
                result.Add(channel.Id);
            }
        }

        return result;
    }

    /// <summary>
    /// Gets the set of tuner host ids the given user is restricted to, or null if the user is unrestricted.
    /// </summary>
    private static HashSet<string>? GetAllowedTunerHostIds(User user)
    {
        if (user is null)
        {
            return null;
        }

        var enabledTunerHostIds = user.GetPreference(PreferenceKind.EnabledTunerHostIds);

        if (enabledTunerHostIds.Length == 0 || user.HasPermission(PermissionKind.EnableAllTunerHosts))
        {
            return null;
        }

        return new HashSet<string>(enabledTunerHostIds, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets a mapping of external channel id to the set of configured tuner host ids that currently carry it.
    /// </summary>
    private async Task<Dictionary<string, HashSet<string>>> GetChannelTunerHostIds(bool enableCache, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var tunerInfo in _config.GetLiveTvConfiguration().TunerHosts)
        {
            var host = _tunerHosts.FirstOrDefault(h => string.Equals(h.Type, tunerInfo.Type, StringComparison.OrdinalIgnoreCase));
            if (host is not IConfiguredTunerChannelProvider provider)
            {
                continue;
            }

            List<ChannelInfo> channels;
            try
            {
                channels = await provider.GetChannels(tunerInfo, enableCache, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting channels for tuner host {TunerId}", tunerInfo.Id);
                continue;
            }

            foreach (var channel in channels)
            {
                if (!result.TryGetValue(channel.Id, out var ids))
                {
                    ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    result[channel.Id] = ids;
                }

                ids.Add(tunerInfo.Id);
            }
        }

        return result;
    }
}

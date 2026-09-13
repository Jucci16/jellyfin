using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;

namespace MediaBrowser.Controller.LiveTv;

/// <summary>
/// Service responsible for managing the <see cref="ITunerHost"/>s.
/// </summary>
public interface ITunerHostManager
{
    /// <summary>
    /// Gets the available <see cref="ITunerHost"/>s.
    /// </summary>
    IReadOnlyList<ITunerHost> TunerHosts { get; }

    /// <summary>
    /// Gets the <see cref="NameIdPair"/>s for the available <see cref="ITunerHost"/>s.
    /// </summary>
    /// <returns>The <see cref="NameIdPair"/>s.</returns>
    IEnumerable<NameIdPair> GetTunerHostTypes();

    /// <summary>
    /// Gets the currently configured <see cref="TunerHostInfo"/> entries.
    /// </summary>
    /// <returns>The configured <see cref="TunerHostInfo"/>s.</returns>
    IReadOnlyList<TunerHostInfo> GetConfiguredTunerHosts();

    /// <summary>
    /// Saves the tuner host.
    /// </summary>
    /// <param name="info">Turner host to save.</param>
    /// <param name="dataSourceChanged">Option to specify that data source has changed.</param>
    /// <returns>Tuner host information wrapped in a task.</returns>
    Task<TunerHostInfo> SaveTunerHost(TunerHostInfo info, bool dataSourceChanged = true);

    /// <summary>
    /// Discovers the available tuners.
    /// </summary>
    /// <param name="newDevicesOnly">A value indicating whether to only return new devices.</param>
    /// <returns>The <see cref="TunerHostInfo"/>s.</returns>
    IAsyncEnumerable<TunerHostInfo> DiscoverTuners(bool newDevicesOnly);

    /// <summary>
    /// Deletes a tuner host by id, cleans up associated caches, and triggers a guide refresh.
    /// </summary>
    /// <param name="id">The tuner host id to delete.</param>
    void DeleteTunerHost(string? id);

    /// <summary>
    /// Scans for tuner devices that have changed URLs.
    /// </summary>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> to use.</param>
    /// <returns>A task that represents the scanning operation.</returns>
    Task ScanForTunerDeviceChanges(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the set of LiveTvChannel library item ids that at least one of the given user's
    /// allowed tuner hosts currently carries.
    /// </summary>
    /// <param name="user">The user to check tuner host access for.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> to use.</param>
    /// <returns>The allowed channel item ids, or <c>null</c> if the user is not restricted to specific tuner hosts.</returns>
    Task<HashSet<Guid>?> GetAllowedChannelItemIds(User user, CancellationToken cancellationToken);
}

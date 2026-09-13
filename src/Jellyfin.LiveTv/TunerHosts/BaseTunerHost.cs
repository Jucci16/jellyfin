#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using Jellyfin.LiveTv.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.TunerHosts
{
    public abstract class BaseTunerHost : IUserAwareTunerHost
    {
        private readonly ConcurrentDictionary<string, List<ChannelInfo>> _cache;

        protected BaseTunerHost(IServerConfigurationManager config, ILogger<BaseTunerHost> logger, IFileSystem fileSystem, IUserManager userManager)
        {
            Config = config;
            Logger = logger;
            FileSystem = fileSystem;
            UserManager = userManager;
            _cache = new ConcurrentDictionary<string, List<ChannelInfo>>();
        }

        protected IServerConfigurationManager Config { get; }

        protected ILogger<BaseTunerHost> Logger { get; }

        protected IFileSystem FileSystem { get; }

        protected IUserManager UserManager { get; }

        public virtual bool IsSupported => true;

        public abstract string Type { get; }

        protected virtual string ChannelIdPrefix => Type + "_";

        protected abstract Task<List<ChannelInfo>> GetChannelsInternal(TunerHostInfo tuner, CancellationToken cancellationToken);

        public async Task<List<ChannelInfo>> GetChannels(TunerHostInfo tuner, bool enableCache, CancellationToken cancellationToken)
        {
            var key = tuner.Id;

            if (enableCache && !string.IsNullOrEmpty(key) && _cache.TryGetValue(key, out List<ChannelInfo> cache))
            {
                return cache;
            }

            var list = await GetChannelsInternal(tuner, cancellationToken).ConfigureAwait(false);
            // logger.LogInformation("Channels from {0}: {1}", tuner.Url, JsonSerializer.SerializeToString(list));

            if (!string.IsNullOrEmpty(key) && list.Count > 0)
            {
                _cache[key] = list;
            }

            return list;
        }

        protected virtual IList<TunerHostInfo> GetTunerHosts()
        {
            return Config.GetLiveTvConfiguration().TunerHosts
                .Where(i => string.Equals(i.Type, Type, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public async Task<List<ChannelInfo>> GetChannels(bool enableCache, CancellationToken cancellationToken)
        {
            var list = new List<ChannelInfo>();

            var hosts = GetTunerHosts();

            foreach (var host in hosts)
            {
                var channelCacheFile = Path.Combine(Config.ApplicationPaths.CachePath, host.Id + "_channels");

                try
                {
                    var channels = await GetChannels(host, enableCache, cancellationToken).ConfigureAwait(false);
                    var newChannels = channels.Where(i => !list.Any(l => string.Equals(i.Id, l.Id, StringComparison.OrdinalIgnoreCase))).ToList();

                    list.AddRange(newChannels);

                    if (!enableCache)
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(channelCacheFile));
                            var writeStream = AsyncFile.OpenWrite(channelCacheFile);
                            await using (writeStream.ConfigureAwait(false))
                            {
                                await JsonSerializer.SerializeAsync(writeStream, channels, cancellationToken: cancellationToken).ConfigureAwait(false);
                            }
                        }
                        catch (IOException)
                        {
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error getting channel list");

                    if (enableCache)
                    {
                        try
                        {
                            var readStream = AsyncFile.OpenRead(channelCacheFile);
                            await using (readStream.ConfigureAwait(false))
                            {
                                var channels = await JsonSerializer
                                    .DeserializeAsync<List<ChannelInfo>>(readStream, cancellationToken: cancellationToken)
                                    .ConfigureAwait(false);
                                list.AddRange(channels);
                            }
                        }
                        catch (IOException)
                        {
                        }
                    }
                }
            }

            return list;
        }

        protected abstract Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(TunerHostInfo tuner, ChannelInfo channel, CancellationToken cancellationToken);

        public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(channelId);

            if (IsValidChannelId(channelId))
            {
                var hosts = GetTunerHosts();

                foreach (var host in hosts)
                {
                    try
                    {
                        var channels = await GetChannels(host, true, cancellationToken).ConfigureAwait(false);
                        var channelInfo = channels.FirstOrDefault(i => string.Equals(i.Id, channelId, StringComparison.OrdinalIgnoreCase));

                        if (channelInfo is not null)
                        {
                            return await GetChannelStreamMediaSources(host, channelInfo, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Error getting channels");
                    }
                }
            }

            return new List<MediaSourceInfo>();
        }

        protected abstract Task<ILiveStream> GetChannelStream(TunerHostInfo tunerHost, ChannelInfo channel, string streamId, IList<ILiveStream> currentLiveStreams, CancellationToken cancellationToken);

        public async Task<ILiveStream> GetChannelStream(string channelId, string streamId, IList<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
            => await GetChannelStreamInternal(channelId, streamId, currentLiveStreams, null, cancellationToken).ConfigureAwait(false);

        public async Task<ILiveStream> GetChannelStream(string channelId, string streamId, Guid userId, IList<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
            => await GetChannelStreamInternal(channelId, streamId, currentLiveStreams, GetAllowedTunerHostIds(userId), cancellationToken).ConfigureAwait(false);

        /// <summary>
        /// Gets the set of tuner host ids the given user is restricted to, or <c>null</c> if the user is unrestricted.
        /// </summary>
        /// <param name="userId">The id of the user requesting the stream.</param>
        /// <returns>A case-insensitive set of allowed tuner host ids, or <c>null</c> if the user has unrestricted access to all tuner hosts.</returns>
        private HashSet<string> GetAllowedTunerHostIds(Guid userId)
        {
            if (userId.IsEmpty())
            {
                return null;
            }

            var user = UserManager.GetUserById(userId);

            var enabledTunerHostIds = user?.GetPreference(PreferenceKind.EnabledTunerHostIds) ?? [];

            if (user is null || enabledTunerHostIds.Length == 0 || user.HasPermission(PermissionKind.EnableAllTunerHosts))
            {
                return null;
            }

            return new HashSet<string>(enabledTunerHostIds, StringComparer.OrdinalIgnoreCase);
        }

        private async Task<ILiveStream> GetChannelStreamInternal(string channelId, string streamId, IList<ILiveStream> currentLiveStreams, HashSet<string> allowedTunerHostIds, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(channelId);

            if (!IsValidChannelId(channelId))
            {
                throw new FileNotFoundException();
            }

            var hosts = GetTunerHosts();

            var hostsWithChannel = new List<Tuple<TunerHostInfo, ChannelInfo>>();

            foreach (var host in hosts)
            {
                try
                {
                    var channels = await GetChannels(host, true, cancellationToken).ConfigureAwait(false);
                    var channelInfo = channels.FirstOrDefault(i => string.Equals(i.Id, channelId, StringComparison.OrdinalIgnoreCase));

                    if (channelInfo is not null)
                    {
                        hostsWithChannel.Add(new Tuple<TunerHostInfo, ChannelInfo>(host, channelInfo));
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error getting channels");
                }
            }

            if (allowedTunerHostIds is not null)
            {
                hostsWithChannel = hostsWithChannel.Where(t => allowedTunerHostIds.Contains(t.Item1.Id)).ToList();
            }

            foreach (var hostTuple in hostsWithChannel)
            {
                var host = hostTuple.Item1;
                var channelInfo = hostTuple.Item2;

                try
                {
                    var liveStream = await GetChannelStream(host, channelInfo, streamId, currentLiveStreams, cancellationToken).ConfigureAwait(false);
                    var startTime = DateTime.UtcNow;
                    await liveStream.Open(cancellationToken).ConfigureAwait(false);
                    var endTime = DateTime.UtcNow;
                    Logger.LogInformation("Live stream opened after {0}ms", (endTime - startTime).TotalMilliseconds);
                    return liveStream;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error opening tuner");
                }
            }

            if (allowedTunerHostIds is not null)
            {
                throw new LiveTvConflictException($"No tuner host assigned to this user is available to stream channel '{channelId}'. The user's assigned tuner(s) may be busy, offline, or do not carry this channel.");
            }

            throw new LiveTvConflictException("Unable to find host to play channel");
        }

        protected virtual bool IsValidChannelId(string channelId)
        {
            ArgumentException.ThrowIfNullOrEmpty(channelId);

            return channelId.StartsWith(ChannelIdPrefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}

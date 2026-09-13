using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.LiveTv.TunerHosts;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.LiveTv.Tests
{
    public class TunerHostManagerTests
    {
        private static readonly TunerHostInfo _tuner1 = new() { Id = "tuner1", Type = "test" };
        private static readonly TunerHostInfo _tuner2 = new() { Id = "tuner2", Type = "test" };

        [Fact]
        public async Task GetAllowedChannelItemIds_UnrestrictedUser_ReturnsNull()
        {
            var manager = CreateManager(new Dictionary<string, List<ChannelInfo>>());
            var user = new User("unrestricted", "auth", "reset");

            var result = await manager.GetAllowedChannelItemIds(user, CancellationToken.None);

            Assert.Null(result);
        }

        [Fact]
        public async Task GetAllowedChannelItemIds_RestrictedUser_ReturnsOnlyChannelsFromAllowedTuner()
        {
            var channel1 = new LiveTvChannel { Id = Guid.NewGuid(), ExternalId = "chan1" };
            var channel2 = new LiveTvChannel { Id = Guid.NewGuid(), ExternalId = "chan2" };

            var manager = CreateManager(
                new Dictionary<string, List<ChannelInfo>>
                {
                    [_tuner1.Id] = [new ChannelInfo { Id = "chan1" }],
                    [_tuner2.Id] = [new ChannelInfo { Id = "chan2" }]
                },
                libraryChannels: [channel1, channel2]);

            var user = CreateRestrictedUser(_tuner2.Id);

            var result = await manager.GetAllowedChannelItemIds(user, CancellationToken.None);

            Assert.NotNull(result);
            Assert.DoesNotContain(channel1.Id, result);
            Assert.Contains(channel2.Id, result);
        }

        [Fact]
        public async Task GetAllowedChannelItemIds_RestrictedUserWithNoMatchingChannels_ReturnsEmptySet()
        {
            var channel1 = new LiveTvChannel { Id = Guid.NewGuid(), ExternalId = "chan1" };

            var manager = CreateManager(
                new Dictionary<string, List<ChannelInfo>>
                {
                    [_tuner1.Id] = [new ChannelInfo { Id = "chan1" }]
                },
                libraryChannels: [channel1]);

            // Restricted to tuner2, which carries nothing in this setup.
            var user = CreateRestrictedUser(_tuner2.Id);

            var result = await manager.GetAllowedChannelItemIds(user, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Empty(result);
        }

        private static User CreateRestrictedUser(string allowedTunerHostId)
        {
            var user = new User("restricted", "auth", "reset");
            user.SetPermission(PermissionKind.EnableAllTunerHosts, false);
            user.SetPreference(PreferenceKind.EnabledTunerHostIds, new[] { allowedTunerHostId });
            return user;
        }

        private static TunerHostManager CreateManager(Dictionary<string, List<ChannelInfo>> channelsByTunerId, IReadOnlyList<BaseItem>? libraryChannels = null)
        {
            var config = new Mock<IConfigurationManager>();
            config.Setup(x => x.GetConfiguration("livetv")).Returns(new LiveTvOptions { TunerHosts = [_tuner1, _tuner2] });

            var tunerHostMock = new Mock<ITunerHost>();
            tunerHostMock.SetupGet(x => x.Type).Returns("test");
            tunerHostMock.SetupGet(x => x.IsSupported).Returns(true);
            tunerHostMock.As<IConfiguredTunerChannelProvider>()
                .Setup(x => x.GetChannels(It.IsAny<TunerHostInfo>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Returns((TunerHostInfo tuner, bool _, CancellationToken _) =>
                    Task.FromResult(channelsByTunerId.TryGetValue(tuner.Id, out var channels) ? channels : []));

            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(libraryChannels ?? []);

            return new TunerHostManager(
                NullLogger<TunerHostManager>.Instance,
                config.Object,
                Mock.Of<ITaskManager>(),
                libraryManager.Object,
                new[] { tunerHostMock.Object });
        }
    }
}

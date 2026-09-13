using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.LiveTv.TunerHosts;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.LiveTv.Tests
{
    public class BaseTunerHostTests
    {
        private const string ChannelId = "test_channel1";

        private static readonly TunerHostInfo _tuner1 = new() { Id = "tuner1", Type = "test" };
        private static readonly TunerHostInfo _tuner2 = new() { Id = "tuner2", Type = "test" };

        [Fact]
        public async Task GetChannelStream_UnrestrictedUser_UsesFirstAvailableTuner()
        {
            var host = CreateHost(failingTunerIds: []);

            var liveStream = await host.GetChannelStream(ChannelId, "streamId", Guid.Empty, [], CancellationToken.None);

            Assert.Equal(_tuner1.Id, liveStream.TunerHostId);
            Assert.Equal([_tuner1.Id], host.AttemptedTunerIds);
        }

        [Fact]
        public async Task GetChannelStream_RestrictedUser_NeverAttemptsDisallowedTuner()
        {
            var host = CreateHost(failingTunerIds: []);
            var userId = RegisterRestrictedUser(host, allowedTunerHostIds: [_tuner2.Id]);

            var liveStream = await host.GetChannelStream(ChannelId, "streamId", userId, [], CancellationToken.None);

            Assert.Equal(_tuner2.Id, liveStream.TunerHostId);
            Assert.Equal([_tuner2.Id], host.AttemptedTunerIds);
        }

        [Fact]
        public async Task GetChannelStream_RestrictedUserWithFailingAssignedTuner_ThrowsWithoutTryingOtherTuners()
        {
            var host = CreateHost(failingTunerIds: [_tuner1.Id]);
            var userId = RegisterRestrictedUser(host, allowedTunerHostIds: [_tuner1.Id]);

            await Assert.ThrowsAsync<LiveTvConflictException>(
                () => host.GetChannelStream(ChannelId, "streamId", userId, [], CancellationToken.None));

            Assert.Equal([_tuner1.Id], host.AttemptedTunerIds);
        }

        /// <summary>
        /// An empty allowlist is treated as "no restriction configured" rather than "block everything".
        /// This is a deliberate safety net: since a permission row absent from the database (e.g. every
        /// pre-existing user before this feature shipped) also reads back as EnableAllTunerHosts == false,
        /// only the presence of at least one assigned tuner id can ever turn on enforcement.
        /// </summary>
        [Fact]
        public async Task GetChannelStream_EmptyAllowedTunerList_IsTreatedAsUnrestricted()
        {
            var host = CreateHost(failingTunerIds: []);
            var userId = RegisterRestrictedUser(host, allowedTunerHostIds: []);

            var liveStream = await host.GetChannelStream(ChannelId, "streamId", userId, [], CancellationToken.None);

            Assert.Equal(_tuner1.Id, liveStream.TunerHostId);
        }

        private static TestableTunerHost CreateHost(IReadOnlyCollection<string> failingTunerIds)
        {
            var config = new Mock<IServerConfigurationManager>();
            config.Setup(x => x.GetConfiguration("livetv")).Returns(new LiveTvOptions { TunerHosts = [_tuner1, _tuner2] });

            var userManagerMock = new Mock<IUserManager>();

            return new TestableTunerHost(config.Object, userManagerMock, failingTunerIds);
        }

        private static Guid RegisterRestrictedUser(TestableTunerHost host, string[] allowedTunerHostIds)
        {
            var user = new User("restricteduser", "authProvider", "resetProvider");
            user.SetPermission(PermissionKind.EnableAllTunerHosts, false);
            user.SetPreference(PreferenceKind.EnabledTunerHostIds, allowedTunerHostIds);

            host.UserManagerMock.Setup(x => x.GetUserById(user.Id)).Returns(user);

            return user.Id;
        }

        /// <summary>
        /// A minimal <see cref="BaseTunerHost"/> that fakes channel discovery/streaming so the
        /// per-user tuner allowlist filtering can be exercised without any real M3U/HDHomeRun I/O.
        /// </summary>
        private sealed class TestableTunerHost : BaseTunerHost
        {
            private readonly HashSet<string> _failingTunerIds;

            public TestableTunerHost(IServerConfigurationManager config, Mock<IUserManager> userManagerMock, IReadOnlyCollection<string> failingTunerIds)
                : base(config, NullLogger<BaseTunerHost>.Instance, Mock.Of<IFileSystem>(), userManagerMock.Object)
            {
                UserManagerMock = userManagerMock;
                _failingTunerIds = new HashSet<string>(failingTunerIds, StringComparer.OrdinalIgnoreCase);
            }

            public Mock<IUserManager> UserManagerMock { get; }

            public List<string> AttemptedTunerIds { get; } = [];

            public override string Type => "test";

            protected override Task<List<ChannelInfo>> GetChannelsInternal(TunerHostInfo tuner, CancellationToken cancellationToken)
                => Task.FromResult(new List<ChannelInfo> { new() { Id = ChannelId } });

            protected override Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(TunerHostInfo tuner, ChannelInfo channel, CancellationToken cancellationToken)
                => Task.FromResult(new List<MediaSourceInfo>());

            protected override Task<ILiveStream> GetChannelStream(TunerHostInfo tunerHost, ChannelInfo channel, string streamId, IList<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
            {
                AttemptedTunerIds.Add(tunerHost.Id);

                if (_failingTunerIds.Contains(tunerHost.Id))
                {
                    throw new InvalidOperationException($"Tuner '{tunerHost.Id}' is unavailable");
                }

                var liveStreamMock = new Mock<ILiveStream>();
                liveStreamMock.SetupGet(x => x.TunerHostId).Returns(tunerHost.Id);
                liveStreamMock.Setup(x => x.Open(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

                return Task.FromResult(liveStreamMock.Object);
            }
        }
    }
}

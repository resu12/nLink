using System.Collections.Concurrent;
using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Infra.DevLocal;

namespace NLink.SmokeTests;

public sealed class RegularV4ToTunaRouteTransitionDevLocalAvailabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferInfraSuite", "RegularV4ToTunaRouteTransition")]
    public async Task PauseBindingRejectsMissingOrStaleExactDataSession(
        bool openStaleSession)
    {
        using var transport = new DevLocalTransport();
        using var dataSession = openStaleSession
            ? await transport.OpenFileTransferDataSessionAsync(
                "route-transition-session",
                "route-transition-transfer",
                CancellationToken.None)
            : null;
        var dataSessionGeneration = dataSession is null
            ? 1
            : GetGeneration(dataSession) + 1;
        var bridge =
            Assert.IsAssignableFrom<IRegularV4ToTunaRouteTransitionBridge>(
                transport);

        var receipt = bridge.BindRegularV4ToTunaActivationPause(
            new RegularV4ToTunaActivationPauseBindCommand(
                CreateAuthority(dataSessionGeneration)));

        Assert.Equal(
            RegularV4ToTunaActivationPauseBindStatus.Rejected,
            receipt.Status);
        Assert.Equal(
            "regular_v4_to_tuna_data_session_not_current",
            receipt.Reason);
        Assert.Null(
            typeof(DevLocalTransport)
                .GetField(
                    "regularV4ToTunaActivationPauseShadow",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(transport));
        if (dataSession is not null)
        {
            Assert.True(dataSession.IsAvailable);
        }
    }

    [Theory]
    [InlineData("Committed", true, FileTransferTransportKind.Tuna)]
    [InlineData("RolledBack", true, FileTransferTransportKind.RegularNkn)]
    [InlineData("TerminalInvalidated", false, FileTransferTransportKind.Unknown)]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferInfraSuite", "RegularV4ToTunaRouteTransition")]
    public async Task ExactDispositionControlsDevLocalDataSessionAvailability(
        string dispositionKind,
        bool releasesAvailability,
        FileTransferTransportKind expectedTransport)
    {
        using var transport = new DevLocalTransport();
        using var dataSession = await transport.OpenFileTransferDataSessionAsync(
            "route-transition-session",
            "route-transition-transfer",
            CancellationToken.None);
        var authority = CreateAuthority(GetGeneration(dataSession));
        var bridge =
            Assert.IsAssignableFrom<IRegularV4ToTunaRouteTransitionBridge>(
                transport);
        var availability = new ConcurrentQueue<
            FileTransferDataSessionAvailabilityChangedEventArgs>();
        dataSession.AvailabilityChanged += (_, args) => availability.Enqueue(args);

        Assert.Equal(
            RegularV4ToTunaActivationPauseBindStatus.Installed,
            bridge.BindRegularV4ToTunaActivationPause(
                new RegularV4ToTunaActivationPauseBindCommand(authority)).Status);
        Assert.False(dataSession.IsAvailable);
        Assert.Empty(availability);

        var kind =
            Enum.Parse<RegularV4ToTunaRouteTransitionDispositionKind>(
                dispositionKind);
        var disposition = kind ==
                          RegularV4ToTunaRouteTransitionDispositionKind.Committed
            ? CreateCommittedDisposition(authority)
            : new RegularV4ToTunaRouteTransitionDispositionCommand(
                authority,
                kind,
                RouteCommitToken: null,
                RoutePublicationToken: null,
                Reason: "test_devlocal_disposition");
        Assert.Equal(
            RegularV4ToTunaRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyRegularV4ToTunaRouteTransitionDisposition(disposition)
                .Status);

        if (!releasesAvailability)
        {
            await Task.Delay(50);
            Assert.False(dataSession.IsAvailable);
            Assert.Empty(availability);
            return;
        }

        var observed = await WaitForAvailabilityAsync(availability);
        Assert.True(dataSession.IsAvailable);
        Assert.True(observed.IsAvailable);
        Assert.Equal(expectedTransport, observed.TargetTransport);
        Assert.False(observed.RequiresResumeRequest);
    }

    [Theory]
    [InlineData("RolledBack", true)]
    [InlineData("TerminalInvalidated", false)]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferInfraSuite", "RegularV4ToTunaRouteTransition")]
    public async Task SupersededDevLocalHandoffWaitsForFinalDisposition(
        string finalDispositionKind,
        bool releasesAvailability)
    {
        using var transport = new DevLocalTransport();
        using var dataSession = await transport.OpenFileTransferDataSessionAsync(
            "route-transition-session",
            "route-transition-transfer",
            CancellationToken.None);
        var authority = CreateAuthority(GetGeneration(dataSession));
        var bridge =
            Assert.IsAssignableFrom<IRegularV4ToTunaRouteTransitionBridge>(
                transport);
        var availability = new ConcurrentQueue<
            FileTransferDataSessionAvailabilityChangedEventArgs>();
        dataSession.AvailabilityChanged += (_, args) => availability.Enqueue(args);

        Assert.Equal(
            RegularV4ToTunaActivationPauseBindStatus.Installed,
            bridge.BindRegularV4ToTunaActivationPause(
                new RegularV4ToTunaActivationPauseBindCommand(authority)).Status);
        Assert.Equal(
            RegularV4ToTunaRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyRegularV4ToTunaRouteTransitionDisposition(
                CreateSupersededDisposition(authority)).Status);
        await Task.Delay(25);
        Assert.False(dataSession.IsAvailable);
        Assert.Empty(availability);

        var finalKind =
            Enum.Parse<RegularV4ToTunaRouteTransitionDispositionKind>(
                finalDispositionKind);
        Assert.Equal(
            RegularV4ToTunaRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyRegularV4ToTunaRouteTransitionDisposition(
                new RegularV4ToTunaRouteTransitionDispositionCommand(
                    authority,
                    finalKind,
                    RouteCommitToken: null,
                    RoutePublicationToken: null,
                    Reason: "test_devlocal_superseded_final")).Status);

        if (releasesAvailability)
        {
            var observed = await WaitForAvailabilityAsync(availability);
            Assert.True(dataSession.IsAvailable);
            Assert.Equal(
                FileTransferTransportKind.RegularNkn,
                observed.TargetTransport);
        }
        else
        {
            await Task.Delay(50);
            Assert.False(dataSession.IsAvailable);
            Assert.Empty(availability);
        }
    }

    private static long GetGeneration(IFileTransferDataSession dataSession)
        => Assert.IsType<long>(
            dataSession.GetType().GetProperty("Generation")!.GetValue(dataSession));

    private static RegularV4ToTunaRouteTransitionAuthority CreateAuthority(
        long dataSessionGeneration)
        => InvokeFactory<RegularV4ToTunaRouteTransitionAuthority>(
            "CreateAuthority",
            "devlocal-availability",
            dataSessionGeneration);

    private static RegularV4ToTunaRouteTransitionDispositionCommand
        CreateCommittedDisposition(
            RegularV4ToTunaRouteTransitionAuthority authority)
        => InvokeFactory<RegularV4ToTunaRouteTransitionDispositionCommand>(
            "CreateCommittedDisposition",
            authority);

    private static RegularV4ToTunaRouteTransitionDispositionCommand
        CreateSupersededDisposition(
            RegularV4ToTunaRouteTransitionAuthority authority)
        => InvokeFactory<RegularV4ToTunaRouteTransitionDispositionCommand>(
            "CreateSupersededDisposition",
            authority);

    private static T InvokeFactory<T>(string methodName, params object[] args)
    {
        var method =
            typeof(RegularV4ToTunaRouteTransitionInfraTests).GetMethod(
                methodName,
                BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<T>(method!.Invoke(null, args));
    }

    private static async Task<FileTransferDataSessionAvailabilityChangedEventArgs>
        WaitForAvailabilityAsync(
            ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>
                events)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (events.TryDequeue(out var observed))
            {
                return observed;
            }

            await Task.Delay(10);
        }

        Assert.Fail("Expected an exact DevLocal transition availability event.");
        return null!;
    }
}

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Loading;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.GraphUseRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Fact]
    public async Task ParticipantDeferredNativeExport_RetainsRetiredRootContextAndGraphUseUntilReleased()
    {
        string? nativePath = null;
        using var fixture = await GraphUseFixture.CreateAsync(
            beforeSharedDependencyCompletionMarker: packagePath =>
                nativePath = NativeLibraryFixtureCompiler.CompileInto(Path.Combine(packagePath, "runtimes",
                    RuntimeInformation.RuntimeIdentifier, "native")),
            emitNativeProbeOnRootSecondV1: true);
        Assert.NotNull(nativePath);
        var nativeDigest = HashFile(nativePath!);

        await using var provider = fixture.CreateProvider("member-b",
            desiredRequests: fixture.Graphs["member-b-v2"].Requests,
            participant: true);
        var loader = provider.GetRequiredService<PackageLoader>();
        PackageGraphUseLeaseOwner? owner = null;
        Type? retainedMarker = null;
        var sentinelReleaseObserved = false;
        try
        {
            var oldGraph = fixture.Graphs["member-b"];
            owner = await AcquireGraphUseAsync(provider, oldGraph);
            var load = await provider.GetRequiredService<IScopedPackageLoader>().EnsureGraphLoadedAsync(
                [oldGraph.CreateEnvelope(owner)], [], CancellationToken.None);
            Assert.Empty(load.FailedByPackageId);
            load = null!;

            provider.GetRequiredService<LoadingCatalogRefreshTracker>().MarkRefreshed("native-library-lifetime-proof");
            retainedMarker = await FindSinglePackageMarkerTypeAsync(provider, "Root.Second");
            Assert.Equal("Root.Second.Marker", retainedMarker.FullName);
            Assert.NotNull(retainedMarker.GetMethod(NativeLibraryFixtureCompiler.MethodName,
                BindingFlags.Public | BindingFlags.Static));

            var contextWeakReference = CaptureFacadeContextWeakReference(
                provider, owner.Lease, retainedMarker, packageId: "Root.Second", version: "1.0.0");
            var leaseSnapshot = owner.Lease.Snapshot;
            Assert.Equal(new[] { "Root.Second", "Shared.Dependency" },
                leaseSnapshot.Nodes.Select(static node => node.Install.PackageId)
                    .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase));

            var initialUses = await InspectGraphUseRecordsAsync(provider.GetRequiredService<IPackageStoreAdmission>(), fixture);
            var initialUse = Assert.Single(initialUses.Entries);
            Assert.Equal(GraphUseRecordOwnershipState.Live, initialUse.OwnershipState);
            Assert.Equal(leaseSnapshot.GraphId, initialUse.Record.GraphSnapshot.GraphId);
            Assert.Equal(leaseSnapshot.GenerationId, initialUse.Record.GraphSnapshot.GenerationId);
            Assert.Equal(leaseSnapshot.Nodes, initialUse.Record.GraphSnapshot.Nodes);
            var useId = initialUse.Record.UseId;
            var sentinelIdentity = initialUse.Record.SentinelIdentity;
            var controlPath = Path.Combine(fixture.RootPath, RootMembershipRegistry.ControlDirectoryName);
            var useRecordPath = Path.Combine(controlPath, $"use-{useId:N}.json");
            var useRecordBytes = await File.ReadAllBytesAsync(useRecordPath);
            Assert.False(await TryAcquireGraphUseSentinelAsync(fixture, useId, sentinelIdentity));

            await owner.DisposeAsync();
            owner = null;
            loader.UnloadContextsNotActive(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

            ForceFullCollection();
            Assert.True(IsWeakTargetAlive(contextWeakReference));
            Assert.True(retainedMarker.IsAssignableTo(typeof(object)));
            await AssertUseRemainsLiveAsync(provider, fixture, leaseSnapshot, useId, sentinelIdentity);

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var generationResult = await provider.GetRequiredService<IReconciliationService>()
                .TriggerAsync(ReconciliationTrigger.Manual("t086-native-library-retire-old-root"), deadline.Token)
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(generationResult.Skipped);
            var committedState = await ReadCoordinatedStateAsync(provider, deadline.Token);
            Assert.Equal("2.0.0", committedState.ActiveVersionById["Root.Second"]);
            Assert.Equal("2.0.0", committedState.LastKnownGoodById["Root.Second"]);
            var verifiedState = PersistedStoreStateGraphVerifier.Verify(committedState);
            var oldRootInstall = leaseSnapshot.Nodes
                .Single(static node => string.Equals(node.Install.PackageId, "Root.Second", StringComparison.OrdinalIgnoreCase))
                .Install;
            Assert.DoesNotContain(verifiedState.ActiveGraphs.Concat(verifiedState.RecoverableGraphs)
                .SelectMany(static graph => graph.Nodes)
                .Select(static node => node.Install), install => install == oldRootInstall);

            var expectedStateBytes = fixture.StatePaths.ToDictionary(static pair => pair.Key,
                static pair => File.ReadAllBytes(pair.Value), StringComparer.Ordinal);
            var expectedInstallEvidence = CaptureFixtureNativeEvidence(fixture);

            // This is deliberately the first call into the packaged native library. The external
            // lease owner is gone and the loader was already asked to retire the old context.
            var nativeResult = InvokeNativeProbe(retainedMarker);
            Assert.Equal(NativeLibraryFixtureCompiler.ExpectedValue, nativeResult);
            Assert.True(IsWeakTargetAlive(contextWeakReference));
            Assert.Equal("Root.Second.Marker", retainedMarker.FullName);
            Assert.Equal(nativeDigest, HashFile(nativePath!));
            Assert.Equal(expectedInstallEvidence.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
                CaptureFixtureNativeEvidence(fixture).OrderBy(static pair => pair.Key, StringComparer.Ordinal));
            foreach (var pair in expectedStateBytes)
                Assert.Equal(pair.Value, await File.ReadAllBytesAsync(fixture.StatePaths[pair.Key]));
            await AssertUseRemainsLiveAsync(provider, fixture, leaseSnapshot, useId, sentinelIdentity);
            Assert.Equal(useRecordBytes, await File.ReadAllBytesAsync(useRecordPath));

            retainedMarker = null;
            await WaitForWeakTargetDeathAsync(contextWeakReference);
            await WaitForGraphUseSentinelReleaseAsync(fixture, useId, sentinelIdentity);
            sentinelReleaseObserved = true;

            var releasedUses = await InspectGraphUseRecordsAsync(provider.GetRequiredService<IPackageStoreAdmission>(), fixture);
            var releasedUse = Assert.Single(releasedUses.Entries, entry => entry.Record.UseId == useId);
            Assert.Equal(GraphUseRecordOwnershipState.Stale, releasedUse.OwnershipState);
            Assert.Equal(sentinelIdentity, releasedUse.Record.SentinelIdentity);
            Assert.Equal(useRecordBytes, await File.ReadAllBytesAsync(useRecordPath));
            Assert.Equal(nativeDigest, HashFile(nativePath!));
            Assert.Equal(expectedInstallEvidence.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
                CaptureFixtureNativeEvidence(fixture).OrderBy(static pair => pair.Key, StringComparer.Ordinal));
            foreach (var pair in expectedStateBytes)
                Assert.Equal(pair.Value, await File.ReadAllBytesAsync(fixture.StatePaths[pair.Key]));
        }
        finally
        {
            retainedMarker = null;
            if (owner is not null)
                await owner.DisposeAsync();
            loader.UnloadContextsNotActive(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            if (!sentinelReleaseObserved)
                await fixture.WaitForGraphUseReleaseAsync(provider.GetRequiredService<IPackageStoreAdmission>());
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<Type> FindSinglePackageMarkerTypeAsync(IServiceProvider provider, string packageId)
    {
        var types = await provider.GetRequiredService<IPackageTypeFinder>()
            .FindTypesAsync<object>(packageId, CancellationToken.None);
        return Assert.Single(types);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int InvokeNativeProbe(Type markerType)
    {
        var method = markerType.GetMethod(NativeLibraryFixtureCompiler.MethodName,
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<int>(method!.Invoke(null, [NativeLibraryFixtureCompiler.InputValue]));
    }

    private static async Task AssertUseRemainsLiveAsync(
        IServiceProvider provider,
        GraphUseFixture fixture,
        PackageGraphUseSnapshot expectedGraph,
        Guid useId,
        PhysicalFileIdentity sentinelIdentity)
    {
        var inspection = await InspectGraphUseRecordsAsync(provider.GetRequiredService<IPackageStoreAdmission>(), fixture);
        var entry = Assert.Single(inspection.Entries, candidate => candidate.Record.UseId == useId);
        Assert.Equal(GraphUseRecordOwnershipState.Live, entry.OwnershipState);
        Assert.Equal(sentinelIdentity, entry.Record.SentinelIdentity);
        Assert.Equal(expectedGraph.GraphId, entry.Record.GraphSnapshot.GraphId);
        Assert.Equal(expectedGraph.GenerationId, entry.Record.GraphSnapshot.GenerationId);
        Assert.Equal(expectedGraph.Nodes, entry.Record.GraphSnapshot.Nodes);
        Assert.False(await TryAcquireGraphUseSentinelAsync(fixture, useId, sentinelIdentity));
    }

    private static async Task<StoreStateRecord> ReadCoordinatedStateAsync(
        IServiceProvider provider,
        CancellationToken cancellationToken)
    {
        var admission = provider.GetRequiredService<IPackageStoreAdmission>();
        await using var operation = await admission.AcquireConfiguredRootOperationAsync(
            PackageStoreAdmissionKind.Maintenance, cancellationToken);
        using var borrow = operation.Owner!.Borrow();
        return await Assert.IsAssignableFrom<ICoordinatedStoreRegistry>(provider.GetRequiredService<IStoreRegistry>())
            .ReadCoordinatedStateAsync(borrow, cancellationToken);
    }

    private static string HashFile(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

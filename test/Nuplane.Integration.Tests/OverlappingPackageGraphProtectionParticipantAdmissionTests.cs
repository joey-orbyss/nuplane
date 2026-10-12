using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Loading;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.GraphUseRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Fact]
    public async Task HostIntegratedParticipantLoad_RefusesCorruptCatalogBeforeCallbacksOrContexts()
    {
        using var fixture = await GraphUseFixture.CreateAsync(PackageLoadMode.HostIntegrated);
        await using var provider = fixture.CreateProvider("member-b", participant: true);

        var corruptPeerPath = fixture.StatePaths["member-a"];
        var originalPeerBytes = await File.ReadAllBytesAsync(corruptPeerPath);
        var originalPeerIdentity = InspectStateFileIdentity(fixture.Files, corruptPeerPath);
        var originalPersistence = CapturePersistenceEvidence(fixture);
        var corruptPeerBytes = await WriteBodyMismatchedStateInPlaceAsync(corruptPeerPath);
        Assert.NotEqual(originalPeerBytes, corruptPeerBytes);
        Assert.Equal(originalPeerIdentity, InspectStateFileIdentity(fixture.Files, corruptPeerPath));

        var corruptPersistence = CapturePersistenceEvidence(fixture);
        var nativeEvidence = CaptureFixtureNativeEvidence(fixture);
        var useArtifacts = ReadUseArtifactNames(fixture);
        Assert.Empty(useArtifacts);
        var packageContexts = CapturePackageContextIdentities(fixture);
        var gate = new ActivationGateProbe();
        var options = new HostIntegratedLoadOptions();
        options.ActivationGates.Add(gate);

        try
        {
            var access = provider.GetRequiredService<NuplaneStoreSnapshotAccess>();
            var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
                NuplaneHostIntegratedLoader.LoadFromParticipantAsync(access, options));

            Assert.Equal(PackageStoreAdmissionReason.StateMismatch, refusal.Reason);
            Assert.Equal(0, gate.CallCount);
            Assert.Equal(packageContexts, CapturePackageContextIdentities(fixture));
            Assert.Equal(useArtifacts, ReadUseArtifactNames(fixture));
            AssertPersistenceEvidenceEqual(corruptPersistence, CapturePersistenceEvidence(fixture));
            AssertNativeEvidenceEqual(nativeEvidence, CaptureFixtureNativeEvidence(fixture));
        }
        finally
        {
            await WriteBytesInPlaceAsync(corruptPeerPath, originalPeerBytes);
        }

        Assert.Equal(originalPeerIdentity, InspectStateFileIdentity(fixture.Files, corruptPeerPath));
        AssertPersistenceEvidenceEqual(originalPersistence, CapturePersistenceEvidence(fixture));
        await using var maintenance = await provider.GetRequiredService<IPackageStoreAdmission>()
            .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance);
        Assert.NotNull(maintenance.Owner);
    }

    [Fact]
    public async Task HostIntegratedParticipantLoad_PreflightsIndependentGraphsBeforeAnyContextOrCallback()
    {
        using var fixture = await GraphUseFixture.CreateAsync(
            PackageLoadMode.HostIntegrated, includeIndependentGraph: true);
        var independent = Assert.IsType<ResolvedGraphFixture>(fixture.IndependentGraph);
        await using var provider = fixture.CreateProvider("member-a", participant: true);

        var published = await PublishProtectedStateAsync(provider, current =>
            fixture.BuildProtectedState("member-a", [fixture.Graphs["member-a"], independent], current,
                checked(current.ProtectionRecord!.Revision + 1), isUpdate: true));
        var protection = Assert.IsType<PackageProtectionRecord>(published.ProtectionRecord);
        Assert.Equal(2, protection.ActiveClosure.Graphs!.Count);
        Assert.Equal(2, published.ActiveGraphsByIdNormalized.Values
            .Count(static graph => graph.Status == GraphActivationStatus.Active));
        var orderedActiveGraphs = published.ActiveGraphsByIdNormalized.Values
            .Where(static graph => graph.Status == GraphActivationStatus.Active)
            .OrderBy(static graph => graph.NodePackageIds.Min(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Contains("Root.First", orderedActiveGraphs[0].NodePackageIds, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Root.Independent", orderedActiveGraphs[1].NodePackageIds, StringComparer.OrdinalIgnoreCase);
        var independentSnapshot = Assert.Single(protection.ActiveClosure.Graphs,
            graph => graph.Nodes.Any(node => string.Equals(node.Install.PackageId, "Root.Independent", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(["Root.Independent"], independentSnapshot.Nodes.Select(static node => node.Install.PackageId));

        var persistenceBefore = CapturePersistenceEvidence(fixture);
        var nativeEvidenceBefore = CaptureFixtureNativeEvidence(fixture);
        var artifactsBefore = ReadUseArtifactNames(fixture);
        Assert.Empty(artifactsBefore);
        var contextsBefore = CapturePackageContextIdentities(fixture);
        var gate = new ActivationGateProbe();
        var options = new HostIntegratedLoadOptions();
        options.ActivationGates.Add(gate);

        var installPath = independent.Packages.Single().InstallPath;
        var markerPath = Path.Combine(installPath, PackageInstallStore.CompletionMarkerFileName);
        var missingMarkerPath = markerPath + ".held-" + Guid.NewGuid().ToString("N");
        File.Move(markerPath, missingMarkerPath);
        try
        {
            var access = provider.GetRequiredService<NuplaneStoreSnapshotAccess>();
            var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
                NuplaneHostIntegratedLoader.LoadFromParticipantAsync(access, options));

            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
            Assert.Equal(0, gate.CallCount);
            Assert.Equal(contextsBefore, CapturePackageContextIdentities(fixture));
            Assert.Equal(artifactsBefore, ReadUseArtifactNames(fixture));
            AssertPersistenceEvidenceEqual(persistenceBefore, CapturePersistenceEvidence(fixture));
        }
        finally
        {
            File.Move(missingMarkerPath, markerPath);
        }

        AssertNativeEvidenceEqual(nativeEvidenceBefore, CaptureFixtureNativeEvidence(fixture));
        await using var maintenance = await provider.GetRequiredService<IPackageStoreAdmission>()
            .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance);
        Assert.NotNull(maintenance.Owner);
    }

    private static async Task<StoreStateRecord> PublishProtectedStateAsync(
        IServiceProvider provider,
        Func<StoreStateRecord, StoreStateRecord> createNextState)
    {
        var admission = provider.GetRequiredService<IPackageStoreAdmission>();
        await using var operation = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Reconciliation);
        using var borrow = operation.Owner!.Borrow();
        var registry = Assert.IsAssignableFrom<ICoordinatedStoreRegistry>(provider.GetRequiredService<IStoreRegistry>());
        var current = await registry.ReadCoordinatedStateAsync(borrow, CancellationToken.None);
        var next = createNextState(current);
        await registry.PersistCoordinatedActiveStateAsync(borrow, next, CancellationToken.None);
        var published = await registry.ReadCoordinatedStateAsync(borrow, CancellationToken.None);
        Assert.Equal(next.ProtectionRecord!.Revision, published.ProtectionRecord!.Revision);
        return published;
    }

    private static string[] CapturePackageContextIdentities(GraphUseFixture fixture)
    {
        var packageIds = fixture.Graphs.Values.SelectMany(static graph => graph.Packages)
            .Select(static package => package.Id)
            .Append("Root.Independent")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return AssemblyLoadContext.All
            .Where(context => context.Assemblies.Any(assembly => packageIds.Contains(assembly.GetName().Name ?? string.Empty)))
            .Select(context => $"{context.Name ?? "unnamed"}:{RuntimeHelpers.GetHashCode(context):x8}")
            .OrderBy(static identity => identity, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task WriteBytesInPlaceAsync(string path, byte[] bytes)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
        file.SetLength(0);
        await file.WriteAsync(bytes);
        await file.FlushAsync();
    }

    private sealed class ActivationGateProbe : IPackagePathIndependentActivationGate
    {
        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<PackageActivationGateResult> EvaluateAsync(
            PackageActivationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult(PackageActivationGateResult.Allow);
        }
    }
}

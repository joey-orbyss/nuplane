using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Maintenance;

namespace Nuplane.Store.Tests.Coordination;

public sealed partial class RootMembershipNativeGroupPublicationTests
{
    [SupportedPhysicalStoreFact]
    public async Task ExecutionBinding_BindsCurrentEligibleRowAndRetainsParentAfterLeafClosure()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        var installPath = CreateInactiveInstall(fixture.Context.RootAPath);

        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive");
            Assert.Equal(PackageStoreRetentionClassification.Eligible, row.Classification);

            using var candidate = scope.BindEligibleCandidate(row, token);
            Assert.Same(row, candidate.PlanEntry);
            Assert.Equal(row.Install, candidate.InstallIdentity);
            Assert.Equal(row.Install.DirectoryIdentity, candidate.Manifest.InstallDirectoryIdentity);
            Assert.Equal(PackageInstallStore.CompletionMarkerFileName,
                Assert.Single(candidate.Manifest.Entries).RelativeComponents.Single());
            var duplicate = Assert.Throws<PackageStoreAdmissionException>(() =>
                scope.BindEligibleCandidate(row, token));
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, duplicate.Reason);

            var parent = candidate.PrepareForMove(token);
            parent.Revalidate(token);
            Assert.Equal("1.0.0", parent.OriginalBasename);
            Assert.Equal(row.Install.DirectoryIdentity, parent.InstallDirectoryIdentity);
            Assert.Equal(row.Install.CompletionIdentity,
                ProtectionDigest.PackageInstallCompletionIdentity(parent.CompletionMarkerIdentity));
            Assert.Equal(parent.PackageParentIdentity,
                fixture.Context.Files.InspectHandle(parent.PackageParentHandle).Identity);

            return Task.FromResult(true);
        });

        Assert.True(Directory.Exists(installPath));
        Assert.Empty(File.ReadAllBytes(Path.Combine(installPath, PackageInstallStore.CompletionMarkerFileName)));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecutionBinding_RejectsCopiedStaleForeignAndRetainedRows()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        using var foreignFixture = await ExecutionFixture.CreateAsync();
        CreateInactiveInstall(fixture.Context.RootAPath);
        CreateInactiveInstall(foreignFixture.Context.RootAPath);

        var preview = await fixture.Inspection.InspectRootAsync("root-0", 0);
        var staleRow = Assert.Single(preview.RetentionPlan!.Entries,
            entry => entry.Install.PackageId == "inactive");
        var foreignRow = await foreignFixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, _) =>
            Task.FromResult(Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive")));

        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var plan = scope.Snapshot.RetentionPlan!;
            var current = Assert.Single(plan.Entries, entry => entry.Install.PackageId == "inactive");
            Assert.Equal(PackageStoreRetentionClassification.Eligible, current.Classification);
            Assert.NotSame(staleRow, current);
            var copiedRow = new PackageStoreRetentionPlanEntry(current.Install, current.Classification, current.Reasons);
            AssertBindingRefused(scope, copiedRow, token);
            AssertBindingRefused(scope, staleRow, token);
            AssertBindingRefused(scope, foreignRow, token);

            var retained = Assert.Single(plan.Entries, entry => entry.Install.PackageId == "Sample.Root");
            Assert.Equal(PackageStoreRetentionClassification.Retained, retained.Classification);
            AssertBindingRefused(scope, retained, token);
            return Task.FromResult(true);
        });

        AssertExecutionLocksReleased(fixture.Context);
        AssertExecutionLocksReleased(foreignFixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecutionBinding_RefusesReplacementAtBoundPathWithoutChangingEitherDirectory()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        var installPath = CreateInactiveInstall(fixture.Context.RootAPath);
        var retainedPath = installPath + ".retained";
        var replacementPayload = Path.Combine(installPath, "replacement.txt");

        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive");
            Directory.Move(installPath, retainedPath);
            Directory.CreateDirectory(installPath);
            File.WriteAllBytes(Path.Combine(installPath, PackageInstallStore.CompletionMarkerFileName), []);
            File.WriteAllText(replacementPayload, "replacement");

            var refusal = Assert.Throws<PackageStoreAdmissionException>(() => scope.BindEligibleCandidate(row, token));
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
            return Task.FromResult(true);
        });

        Assert.True(File.Exists(Path.Combine(retainedPath, PackageInstallStore.CompletionMarkerFileName)));
        Assert.Empty(File.ReadAllBytes(Path.Combine(retainedPath, PackageInstallStore.CompletionMarkerFileName)));
        Assert.Equal("replacement", File.ReadAllText(replacementPayload));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecutionBinding_PreparedCandidateSupportsExistingHeldParentNoReplaceMove()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        var installPath = CreateInactiveInstall(fixture.Context.RootAPath);
        File.WriteAllText(Path.Combine(installPath, "payload.txt"), "quarantine payload");

        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive");
            var candidate = scope.BindEligibleCandidate(row, token);
            var parent = candidate.PrepareForMove(token);
            var quarantineName = "quarantine-" + Guid.NewGuid().ToString("N");
            var publication = Assert.IsAssignableFrom<IPhysicalStoreDirectoryPublicationFileSystem>(fixture.Context.Files);

            // This proves handle closure for the existing kernel in an owned fixture only; it is not
            // a durable Prepared-journal receipt or authorization for an executor to move installs.
            var moved = publication.PublishDirectoryNoReplaceAt(parent.PackageParentHandle,
                parent.OriginalBasename, parent.InstallDirectoryIdentity, quarantineName);

            Assert.Equal(parent.InstallDirectoryIdentity, moved.Identity);
            Assert.Null(fixture.Context.Files.InspectChildNoFollow(parent.PackageParentHandle, parent.OriginalBasename));
            var named = Assert.IsType<PhysicalStoreEntryInfo>(
                fixture.Context.Files.InspectChildNoFollow(parent.PackageParentHandle, quarantineName));
            Assert.Equal(PhysicalStoreEntryKind.Directory, named.Kind);
            Assert.Equal(parent.InstallDirectoryIdentity, named.Identity);
            using var movedDirectory = fixture.Context.Files.OpenDirectoryChildNoFollow(
                parent.PackageParentHandle, quarantineName);
            var movedMarker = Assert.IsType<PhysicalStoreEntryInfo>(fixture.Context.Files.InspectChildNoFollow(
                movedDirectory, PackageInstallStore.CompletionMarkerFileName));
            Assert.Equal(PhysicalStoreEntryKind.RegularFile, movedMarker.Kind);
            Assert.Equal(parent.CompletionMarkerIdentity, movedMarker.Identity);
            using var marker = fixture.Context.Files.OpenFileChildNoFollow(
                movedDirectory, PackageInstallStore.CompletionMarkerFileName, FileAccess.Read);
            Assert.Equal(0, fixture.Context.Files.InspectHandle(marker).Length);
            Assert.Equal("quarantine payload", File.ReadAllText(Path.Combine(
                fixture.Context.RootAPath, "feed", "inactive", quarantineName, "payload.txt")));
            parent.Revalidate(token);
            scope.Revalidate(token);
            Assert.Equal(fixture.Context.RootAIdentity.HandleIdentity,
                fixture.Context.Files.InspectHandle(scope.RootHandle).Identity);
            return Task.FromResult(true);
        });

        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecutionBinding_RefusesChangedPackageParentAfterBinding()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        var installPath = CreateInactiveInstall(fixture.Context.RootAPath);
        File.WriteAllText(Path.Combine(installPath, "payload.txt"), "original parent");
        var packagePath = Path.GetDirectoryName(installPath)!;
        var movedPackagePath = packagePath + ".moved";

        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive");
            var candidate = scope.BindEligibleCandidate(row, token);
            try
            {
                Directory.Move(packagePath, movedPackagePath);
            }
            catch (Exception exception) when (OperatingSystem.IsWindows() &&
                                               (exception is IOException or UnauthorizedAccessException))
            {
                candidate.Revalidate(token);
                Assert.True(Directory.Exists(packagePath));
                return Task.FromResult(true);
            }

            Directory.CreateDirectory(packagePath);
            var refusal = Assert.Throws<PackageStoreAdmissionException>(() => candidate.Revalidate(token));
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
            Assert.Equal("original parent", File.ReadAllText(Path.Combine(movedPackagePath, "1.0.0", "payload.txt")));
            Assert.False(Directory.Exists(Path.Combine(packagePath, "1.0.0")));
            return Task.FromResult(true);
        });

        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecutionBinding_RefusesReplacedCompletionMarkerWhilePreservingBothNames()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        var installPath = CreateInactiveInstall(fixture.Context.RootAPath);
        var markerPath = Path.Combine(installPath, PackageInstallStore.CompletionMarkerFileName);
        var savedMarkerPath = Path.Combine(installPath, "saved-marker");

        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive");
            var candidate = scope.BindEligibleCandidate(row, token);
            try
            {
                File.Move(markerPath, savedMarkerPath);
            }
            catch (Exception exception) when (OperatingSystem.IsWindows() &&
                                               (exception is IOException or UnauthorizedAccessException))
            {
                candidate.Revalidate(token);
                Assert.Empty(File.ReadAllBytes(markerPath));
                return Task.FromResult(true);
            }

            File.WriteAllBytes(markerPath, []);
            var refusal = Assert.Throws<PackageStoreAdmissionException>(() => candidate.Revalidate(token));
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
            Assert.Empty(File.ReadAllBytes(savedMarkerPath));
            Assert.Empty(File.ReadAllBytes(markerPath));
            return Task.FromResult(true);
        });

        AssertExecutionLocksReleased(fixture.Context);
    }

    [LinuxExt4CasefoldFact]
    public async Task ExecutionBinding_RefusesCasefoldAliasOfCurrentEligiblePath()
    {
        using var fixture = await ExecutionFixture.CreateAsync(context =>
        {
            var feedPath = Path.Combine(context.RootAPath, "feed");
            Directory.CreateDirectory(feedPath);
            UnixPhysicalStoreIdentityTests.EnableExt4Casefold(feedPath);
            CreateInactiveInstall(context.RootAPath);
            return Task.FromResult(Array.Empty<string>());
        }, withCrossRootGraph: false);
        var packagePath = Path.Combine(fixture.Context.RootAPath, "feed", "inactive");
        var aliasPath = Path.Combine(fixture.Context.RootAPath, "feed", "INACTIVE");

        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive");
            Assert.Equal(PackageStoreRetentionClassification.Eligible, row.Classification);
            Directory.Move(packagePath, aliasPath);

            var refusal = Assert.Throws<PackageStoreAdmissionException>(() =>
                scope.BindEligibleCandidate(row, token));
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
            Assert.Empty(File.ReadAllBytes(Path.Combine(aliasPath, "1.0.0", PackageInstallStore.CompletionMarkerFileName)));
            return Task.FromResult(true);
        });

        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecutionBinding_FreshProtectionRetainsEarlierPreviewCandidate()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        var installPath = CreateInactiveInstall(fixture.Context.RootAPath, "Sample.Root", "2.0.0");
        File.WriteAllText(Path.Combine(installPath, "payload.txt"), "preview payload");
        var preview = await fixture.Inspection.InspectRootAsync("root-0", 0);
        var previewRow = Assert.Single(preview.RetentionPlan!.Entries,
            entry => entry.Install.PackageId == "Sample.Root" && entry.Install.Version == "2.0.0");
        Assert.Equal(PackageStoreRetentionClassification.Eligible, previewRow.Classification);
        var next = fixture.Context.PrepareNextGraphPublication(previewRow.Install);
        await fixture.Context.Registry().PublishNativeGroupAsync(next.Descriptor, fixture.Context.Requests,
            next.NextState, CancellationToken.None);

        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var current = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "Sample.Root" && entry.Install.Version == "2.0.0");
            Assert.Equal(PackageStoreRetentionClassification.Retained, current.Classification);
            Assert.Contains(PackageStoreRetentionReason.ProtectedActive, current.Reasons);
            AssertBindingRefused(scope, previewRow, token);
            AssertBindingRefused(scope, current, token);
            return Task.FromResult(true);
        });

        Assert.Equal("preview payload", File.ReadAllText(Path.Combine(installPath, "payload.txt")));
        Assert.Empty(File.ReadAllBytes(Path.Combine(installPath, PackageInstallStore.CompletionMarkerFileName)));
        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecutionBinding_ParentBindingExpiresWhenCandidateIsDisposed()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        CreateInactiveInstall(fixture.Context.RootAPath);
        PackageStoreDeletionParentBinding? escapedBinding = null;

        await fixture.Service.PlanAndRunAsync("root-0", 1, 0, (scope, token) =>
        {
            var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                entry => entry.Install.PackageId == "inactive");
            var candidate = scope.BindEligibleCandidate(row, token);
            escapedBinding = candidate.PrepareForMove(token);
            candidate.Dispose();

            var expired = Assert.Throws<PackageStoreAdmissionException>(() =>
                escapedBinding!.Revalidate(CancellationToken.None));
            Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, expired.Reason);
            return Task.FromResult(true);
        });

        AssertExecutionLocksReleased(fixture.Context);
    }

    [SupportedPhysicalStoreFact]
    public async Task ExecutionBinding_CallbackFailureDisposesEscapedParentAndAncestry()
    {
        using var fixture = await ExecutionFixture.CreateAsync();
        CreateInactiveInstall(fixture.Context.RootAPath);
        PackageStoreDeletionParentBinding? escapedBinding = null;
        PhysicalStoreDirectoryHandle? escapedParent = null;
        var failure = new InvalidOperationException("candidate-callback-failure");

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.PlanAndRunAsync<bool>("root-0", 1, 0, (scope, token) =>
            {
                var row = Assert.Single(scope.Snapshot.RetentionPlan!.Entries,
                    entry => entry.Install.PackageId == "inactive");
                var candidate = scope.BindEligibleCandidate(row, token);
                escapedBinding = candidate.PrepareForMove(token);
                escapedParent = escapedBinding.PackageParentHandle;
                throw failure;
            }));

        Assert.Same(failure, actual);
        var expiredBinding = Assert.Throws<PackageStoreAdmissionException>(() => escapedBinding!.Revalidate(CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, expiredBinding.Reason);
        var expiredHandle = Assert.Throws<PackageStoreAdmissionException>(() =>
            fixture.Context.Files.InspectHandle(escapedParent!));
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, expiredHandle.Reason);
        AssertExecutionLocksReleased(fixture.Context);
    }

    private static void AssertBindingRefused(
        PackageStoreExecutionService.Context scope,
        PackageStoreRetentionPlanEntry row,
        CancellationToken cancellationToken)
    {
        var refusal = Assert.Throws<PackageStoreAdmissionException>(() =>
            scope.BindEligibleCandidate(row, cancellationToken));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
    }

    private static string CreateInactiveInstall(string rootPath, string packageId = "inactive", string version = "1.0.0")
    {
        var installPath = Path.Combine(rootPath, "feed", packageId, version);
        Directory.CreateDirectory(installPath);
        File.WriteAllBytes(Path.Combine(installPath, PackageInstallStore.CompletionMarkerFileName), []);
        return installPath;
    }
}

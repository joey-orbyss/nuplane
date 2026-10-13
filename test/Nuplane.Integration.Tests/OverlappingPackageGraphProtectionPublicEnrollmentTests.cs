using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nuplane.Admin;
using Nuplane.Admin.Api;
using Nuplane.Builder;
using Nuplane.Feeds.Configuration;
using Nuplane.Integration.Tests.Support;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Maintenance;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Fact]
    public async Task PublicEnrollmentApi_QuiescentTwoMemberCutoverCompletesAndEmptyPrunePreservesState()
    {
        using var fixture = new PackageStoreFixture();
        var secondState = fixture.CreateStateSlot("second/store-state.json");
        await using var provider = CreateEnrollmentProvider(fixture,
            [new("default", "first", fixture.StateFilePath), new("default", "second", secondState)]);
        var operations = provider.GetRequiredService<INuplanePackageStoreMaintenanceOperations>();
        var preview = await operations.PreviewEnrollmentAsync();
        Assert.Equal("Unenrolled", preview.Membership);
        Assert.NotNull(preview.RootIdentity);
        Assert.True(preview.IsNonQuiescentObservation);
        Assert.NotNull(preview.CurrentMemberIds);
        Assert.Empty(preview.CurrentMemberIds);
        Assert.Equal(2, preview.Members.Count);
        Assert.False(File.Exists(fixture.StateFilePath));
        Assert.False(File.Exists(secondState));
        var request = new PackageStoreEnrollmentRequest("default", preview.RootIdentity, 1,
            false, ["first", "second"], []);
        using var app = EndpointRouteTestHarness.CreateApp(registered => registered.AddSingleton(operations),
            endpoints => endpoints.MapNuplanePackageStoreMaintenance());

        var refused = await EndpointRouteTestHarness.InvokeAsync(app, "/nuplane/admin/store/enrollment", "POST", JsonBody(request));
        Assert.Equal(StatusCodes.Status409Conflict, refused.StatusCode);
        Assert.False(File.Exists(fixture.StateFilePath));
        Assert.False(File.Exists(secondState));

        var completed = await EndpointRouteTestHarness.InvokeAsync(app, "/nuplane/admin/store/enrollment", "POST",
            JsonBody(request with { QuiescentCutoverConfirmed = true }));
        using var report = JsonDocument.Parse(completed.Body);
        Assert.Equal(StatusCodes.Status200OK, completed.StatusCode);
        Assert.Equal("Complete", report.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, report.RootElement.GetProperty("enrollmentEpoch").GetInt64());
        var enrolled = await operations.PreviewEnrollmentAsync();
        Assert.Equal(1, enrolled.EnrollmentEpoch);
        Assert.Equal(new[] { "first", "second" }, enrolled.CurrentMemberIds);
        var firstBytes = File.ReadAllBytes(fixture.StateFilePath);
        var secondBytes = File.ReadAllBytes(secondState);
        var serializer = new StoreStateSerializer();
        Assert.Equal("first", (await serializer.LoadAsync(fixture.StateFilePath, CancellationToken.None)).ProtectionRecord!.MemberId);
        Assert.Equal("second", (await serializer.LoadAsync(secondState, CancellationToken.None)).ProtectionRecord!.MemberId);

        var plan = await operations.PruneAsync(new PackageStorePruneRequest(Retention: new(0)));
        var noOp = await operations.PruneAsync(Confirm(plan, new(0)));
        Assert.Equal("Completed", noOp.Status);
        Assert.True(noOp.InventoryComplete);
        Assert.Empty(noOp.Candidates);
        Assert.Equal(firstBytes, File.ReadAllBytes(fixture.StateFilePath));
        Assert.Equal(secondBytes, File.ReadAllBytes(secondState));
        var repeat = await operations.EnrollAsync(request with { QuiescentCutoverConfirmed = true });
        Assert.Equal("Refused", repeat.Status);
        Assert.Equal(firstBytes, File.ReadAllBytes(fixture.StateFilePath));
        Assert.Equal(secondBytes, File.ReadAllBytes(secondState));
    }

    [Fact]
    public async Task PublicEnrollmentApi_MigrationMovesStateAndRequiresExplicitMemberRetirement()
    {
        using var fixture = new PackageStoreFixture();
        var secondState = fixture.CreateStateSlot("second/store-state.json");
        await EnrollEmptyFixtureAsync(fixture,
            [new("default", "first", fixture.StateFilePath), new("default", "second", secondState)]);

        var originalFirstBytes = File.ReadAllBytes(fixture.StateFilePath);
        var originalSecondBytes = File.ReadAllBytes(secondState);
        var movedFirstState = fixture.CreateStateSlot("moved/store-state.json");
        var thirdState = fixture.CreateStateSlot("third/store-state.json");
        await using var provider = CreateEnrollmentProvider(fixture,
            [new("default", "first", movedFirstState), new("default", "third", thirdState)]);
        var operations = provider.GetRequiredService<INuplanePackageStoreMaintenanceOperations>();
        var observed = await operations.PreviewEnrollmentAsync();
        Assert.Equal(new[] { "first", "second" }, observed.CurrentMemberIds);
        Assert.Equal(1, observed.EnrollmentEpoch);
        var request = new PackageStoreEnrollmentRequest("default", observed.RootIdentity!, 1,
            true, ["first", "third"], ["first", "second"]);
        using var app = EndpointRouteTestHarness.CreateApp(registered => registered.AddSingleton(operations),
            endpoints => endpoints.MapNuplanePackageStoreMaintenance());

        var refused = await EndpointRouteTestHarness.InvokeAsync(app, "/nuplane/admin/store/enrollment/migrate", "POST", JsonBody(request));
        Assert.Equal(StatusCodes.Status409Conflict, refused.StatusCode);
        Assert.False(File.Exists(movedFirstState));
        Assert.False(File.Exists(thirdState));

        var migrated = await EndpointRouteTestHarness.InvokeAsync(app, "/nuplane/admin/store/enrollment/migrate", "POST",
            JsonBody(request with { RetireMemberIds = ["second"] }));
        Assert.Equal(StatusCodes.Status200OK, migrated.StatusCode);
        using var report = JsonDocument.Parse(migrated.Body);
        Assert.Equal("Complete", report.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, report.RootElement.GetProperty("enrollmentEpoch").GetInt64());
        var current = await operations.PreviewEnrollmentAsync();
        Assert.Equal("Complete", current.Membership);
        Assert.Equal(2, current.EnrollmentEpoch);
        Assert.Equal(new[] { "first", "third" }, current.CurrentMemberIds);
        Assert.All(current.Members, member => Assert.Equal("Observed", member.Observation));
        var serializer = new StoreStateSerializer();
        Assert.Equal("first", (await serializer.LoadAsync(movedFirstState, CancellationToken.None)).ProtectionRecord!.MemberId);
        Assert.Equal("third", (await serializer.LoadAsync(thirdState, CancellationToken.None)).ProtectionRecord!.MemberId);
        Assert.Equal(originalFirstBytes, File.ReadAllBytes(fixture.StateFilePath));
        Assert.Equal(originalSecondBytes, File.ReadAllBytes(secondState));
    }

    [Fact]
    public async Task PublicEnrollmentApi_RecoversInterruptedMigrationUsingObservedEpochAndRoster()
    {
        using var fixture = new PackageStoreFixture();
        var secondState = fixture.CreateStateSlot("second/store-state.json");
        await EnrollEmptyFixtureAsync(fixture,
            [new("default", "first", fixture.StateFilePath), new("default", "second", secondState)]);
        var firstBytes = File.ReadAllBytes(fixture.StateFilePath);
        var secondBytes = File.ReadAllBytes(secondState);
        var movedFirst = fixture.CreateStateSlot("moved/store-state.json");
        var thirdState = fixture.CreateStateSlot("third/store-state.json");
        await using var provider = CreateEnrollmentProvider(fixture,
            [new("default", "first", movedFirst), new("default", "third", thirdState)]);
        var operations = provider.GetRequiredService<INuplanePackageStoreMaintenanceOperations>();
        var before = await operations.PreviewEnrollmentAsync();
        // Establish a real interrupted on-disk migration through the core's fault-injection seam.
        // The public recovery still uses the supported built-in serializer/composition.
        var files = provider.GetRequiredService<IPhysicalStoreFileSystem>();
        var serializer = new InterruptingMemberStateSerializer("third", expectedEpoch: 2);
        var faultingMigration = new PackageStoreEnrollmentService(files, new RootMembershipRegistry(files, serializer),
            serializer, provider.GetRequiredService<ITrustedPackageStoreRootCatalog>(),
            provider.GetRequiredService<IPackageStoreRootResolver>(),
            provider.GetRequiredService<ITrustedPackageStoreMemberDeclarationCatalog>(),
            provider.GetRequiredService<IOptions<FeedResolutionOptions>>().Value);
        await Assert.ThrowsAsync<IOException>(() => faultingMigration.MigrateAsync(new("default", before.RootIdentity!,
            before.EnrollmentEpoch!.Value, true, ["first", "third"], before.CurrentMemberIds!, ["second"])));
        Assert.True(File.Exists(movedFirst));
        Assert.False(File.Exists(thirdState));
        var observed = await operations.PreviewEnrollmentAsync();
        Assert.Equal("Incomplete", observed.Membership);
        Assert.Equal(2, observed.EnrollmentEpoch);
        Assert.Equal(new[] { "first", "second", "third" }, observed.CurrentMemberIds);
        Assert.Contains(observed.Diagnostics, static message => message.Contains("PendingMembershipMigrationRecoveryRequired", StringComparison.Ordinal));
        var request = new PackageStoreEnrollmentRequest("default", observed.RootIdentity!, observed.EnrollmentEpoch!.Value,
            true, ["first", "third"], observed.CurrentMemberIds!, ["second"]);
        using var app = EndpointRouteTestHarness.CreateApp(registered => registered.AddSingleton(operations),
            endpoints => endpoints.MapNuplanePackageStoreMaintenance());

        var stale = await EndpointRouteTestHarness.InvokeAsync(app, "/nuplane/admin/store/enrollment/recover", "POST",
            JsonBody(request with { ExpectedEnrollmentEpoch = 1 }));
        Assert.Equal(StatusCodes.Status409Conflict, stale.StatusCode);
        var response = await EndpointRouteTestHarness.InvokeAsync(app, "/nuplane/admin/store/enrollment/recover", "POST", JsonBody(request));
        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        var complete = await operations.PreviewEnrollmentAsync();
        Assert.Equal("Complete", complete.Membership);
        Assert.Equal(2, complete.EnrollmentEpoch);
        Assert.Equal(new[] { "first", "third" }, complete.CurrentMemberIds);
        Assert.Equal(firstBytes, File.ReadAllBytes(fixture.StateFilePath));
        Assert.Equal(secondBytes, File.ReadAllBytes(secondState));
        Assert.True(File.Exists(thirdState));
    }

    [Fact]
    public async Task PublicEnrollmentApi_RecoveryGraphTransportRoundtripsAndRejectsNestedUnknownFields()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = CreateMaintenanceProvider(fixture);
        var operations = provider.GetRequiredService<INuplanePackageStoreMaintenanceOperations>();
        var preview = await operations.PreviewEnrollmentAsync();
        var observed = Assert.Single(preview.Members, static member => member.MemberId == "member-a");
        var state = await new StoreStateSerializer().LoadAsync(fixture.StatePaths["member-a"], CancellationToken.None);
        var protection = state.ProtectionRecord!;
        var evidence = new PackageStoreRecoveryEvidence("member-a", observed.StateSlot!, observed.StateFileIdentity!,
            observed.StateBodyDigest!, protection.ActiveClosure.Graphs!.Select(Transport).ToArray(),
            protection.RecoverableClosure.Graphs!.Select(Transport).ToArray());
        var request = new PackageStoreEnrollmentRequest("default", fixture.RootIdentity, preview.EnrollmentEpoch!.Value,
            true, ["member-a", "member-b"], ["member-a", "member-b"], RecoveryEvidence: [evidence]);
        var body = JsonBody(request);
        var roundtrip = JsonSerializer.Deserialize<PackageStoreEnrollmentRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var graph = Assert.Single(roundtrip!.RecoveryEvidence![0].ActiveGraphs);
        Assert.NotEmpty(graph.RequestedRoots);
        Assert.NotEmpty(graph.Nodes);
        Assert.Equal(evidence.ActiveGraphs[0].GraphId, graph.GraphId);
        using var app = EndpointRouteTestHarness.CreateApp(registered => registered.AddSingleton(operations),
            endpoints => endpoints.MapNuplanePackageStoreMaintenance());
        var before = CaptureFixtureNativeEvidence(fixture);
        // This root is already Complete: valid transport reaches the core's explicit refusal.
        var response = await EndpointRouteTestHarness.InvokeAsync(app, "/nuplane/admin/store/enrollment/recover", "POST", body);
        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        var modified = System.Text.Json.Nodes.JsonNode.Parse(body)!;
        modified["recoveryEvidence"]![0]!["activeGraphs"]![0]!["nodes"]![0]!["install"]!["rootPath"] = "/untrusted";
        var rejected = await EndpointRouteTestHarness.InvokeAsync(app, "/nuplane/admin/store/enrollment/recover", "POST", modified.ToJsonString());
        Assert.Equal(StatusCodes.Status400BadRequest, rejected.StatusCode);
        Assert.Equal(before.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
            CaptureFixtureNativeEvidence(fixture).OrderBy(static pair => pair.Key, StringComparer.Ordinal));
    }

    private static string JsonBody(PackageStoreEnrollmentRequest request)
        => JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static ServiceProvider CreateEnrollmentProvider(PackageStoreFixture fixture,
        PackageStoreMemberDeclaration[] declarations)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(builder => builder.WithStateFile(declarations[0].StateFilePath)
            .UseCoordinatedPackageStore(declarations, true));
        services.Configure<FeedResolutionOptions>(options => options.PackageInstallRoot = fixture.PackageInstallRoot);
        services.AddNuplanePackageStoreMaintenance();
        return services.BuildServiceProvider();
    }

    private static async Task EnrollEmptyFixtureAsync(PackageStoreFixture fixture,
        PackageStoreMemberDeclaration[] declarations)
    {
        await using var provider = CreateEnrollmentProvider(fixture, declarations);
        var operations = provider.GetRequiredService<INuplanePackageStoreMaintenanceOperations>();
        var preview = await operations.PreviewEnrollmentAsync();
        var enrolled = await operations.EnrollAsync(new("default", preview.RootIdentity!, 1,
            true, declarations.Select(static member => member.MemberId).ToArray(), []));
        Assert.Equal("Complete", enrolled.Status);
    }

    private static PackageStoreRecoveryGraphEvidence Transport(Nuplane.Store.Coordination.ProtectionRecords.ProtectedGraphSnapshot graph)
        => new(graph.SnapshotId, graph.GraphId, graph.GenerationId, graph.Disposition, graph.Roots,
            graph.RequestedRoots.Select(static root => new PackageStoreRecoveryRootSelection(root.Request, root.SelectedNodeId)).ToArray(),
            graph.Nodes.Select(static node => new PackageStoreRecoveryNode(node.NodeId, node.Install)).ToArray(),
            graph.Edges.Select(static edge => new PackageStoreRecoveryEdge(edge.FromNodeId, edge.ToNodeId,
                edge.RequestedPackageId, edge.RequestedVersionRange, edge.TargetFramework, edge.IsOptional)).ToArray(),
            graph.RecoverySelectionEvidence is { } source
                ? new(source.RecoveryPolicyId, source.SourceRevision, source.SelectedRootNodeIds) : null);
}

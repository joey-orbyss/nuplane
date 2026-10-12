using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane;
using Nuplane.Feeds;
using Nuplane.Feeds.Configuration;
using Nuplane.Loading;
using Nuplane.Loading.Hosting.Builder;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.LockFile;
using Nuplane.Reconciliation.Models;
using Nuplane.Builder;
using Nuplane.Registration;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.GraphUseRecords;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.PackageStore.TestHost;

internal static partial class Program
{
    private static async Task<int> RunGraphUseIdleAsync(string requestPath, string mode)
    {
        var request = await ReadGraphUseRequestAsync(requestPath).ConfigureAwait(false);
        await WriteJsonLineAsync(new StartedMessage("started", request.OperationId, Environment.ProcessId)).ConfigureAwait(false);

        var provider = BuildGraphUseProvider(request, request.CompleteRoster);
        var isParticipantLoad = mode != "--graph-use-idle";
        var didParticipantRepeatCompositionAcceptance = false;
        var didActivationGateProbeLiveUse = false;
        var activationGateEvaluated = false;
        if (!isParticipantLoad)
        {
            var result = await provider.GetRequiredService<IReconciliationService>()
                .TriggerAsync(ReconciliationTrigger.Manual("graph-use-process-exit"), CancellationToken.None)
                .ConfigureAwait(false);
            if (result.Skipped || result.IsDegraded || result.FailedPackages.Count != 0)
                throw new InvalidOperationException(
                    $"Host-integrated reconciliation did not complete cleanly (skipped={result.Skipped}, degraded={result.IsDegraded}, failures={string.Join(",", result.FailedPackages)}).");
        }
        else
        {
            var access = provider.GetRequiredService<NuplaneStoreSnapshotAccess>();
            if (mode == "--participant-graph-use-unscoped-gate")
            {
                var gate = new UnscopedActivationGate();
                var options = new HostIntegratedLoadOptions();
                options.ActivationGates.Add(gate);
                try
                {
                    await NuplaneHostIntegratedLoader.LoadFromParticipantAsync(access, options).ConfigureAwait(false);
                    throw new InvalidOperationException("An unscoped activation gate was unexpectedly accepted for participant loading.");
                }
                catch (PackageStoreAdmissionException exception) when
                    (exception.Reason == PackageStoreAdmissionReason.UnsupportedParticipant)
                {
                    // The protected loader must reject the gate type before invoking user code.
                }

                activationGateEvaluated = gate.WasEvaluated;
                if (activationGateEvaluated)
                    throw new InvalidOperationException("The unscoped activation gate ran before participant rejection.");
            }
            else if (mode == "--participant-graph-use-reordered-roster")
            {
                var firstLoad = await NuplaneHostIntegratedLoader.LoadFromParticipantAsync(access).ConfigureAwait(false);
                if (firstLoad.FailedByPackageId.Count != 0)
                    throw new InvalidOperationException($"The first participant load failed: {string.Join(",", firstLoad.FailedByPackageId.Keys)}.");

                await using var reorderedProvider = BuildGraphUseProvider(request, request.CompleteRoster.Reverse().ToArray());
                var repeatedLoad = await NuplaneHostIntegratedLoader.LoadFromParticipantAsync(
                    reorderedProvider.GetRequiredService<NuplaneStoreSnapshotAccess>()).ConfigureAwait(false);
                if (repeatedLoad.FailedByPackageId.Count != 0)
                    throw new InvalidOperationException(
                        $"A repeat participant load with the same declarations in a different order failed: {string.Join(",", repeatedLoad.FailedByPackageId.Keys)}.");
                didParticipantRepeatCompositionAcceptance = true;
            }
            else if (mode == "--participant-graph-use-scoped-gate")
            {
                var gate = new ParticipantSnapshotProbeGate(
                    access, provider.GetRequiredService<IPackageStoreAdmission>());
                var options = new HostIntegratedLoadOptions();
                options.ActivationGates.Add(gate);
                var result = await NuplaneHostIntegratedLoader.LoadFromParticipantAsync(access, options).ConfigureAwait(false);
                if (result.FailedByPackageId.Count != 0 || !gate.CompletedLiveUseProbe)
                    throw new InvalidOperationException("The scoped gate did not observe its committed live graph-use record after the short catalog snapshot was released.");
                didActivationGateProbeLiveUse = true;
            }
            else
            {
                var firstLoad = await NuplaneHostIntegratedLoader.LoadFromParticipantAsync(access).ConfigureAwait(false);
                var repeatedLoad = await NuplaneHostIntegratedLoader.LoadFromParticipantAsync(access).ConfigureAwait(false);
                if (firstLoad.FailedByPackageId.Count != 0 || repeatedLoad.FailedByPackageId.Count != 0)
                    throw new InvalidOperationException(
                        $"Participant host-integrated loading did not complete cleanly (first={string.Join(",", firstLoad.FailedByPackageId.Keys)}, repeat={string.Join(",", repeatedLoad.FailedByPackageId.Keys)}).");
            }
        }

        var requiredAssemblies = new[] { "Root.First", "Shared.Dependency" };
        var matchingContexts = AssemblyLoadContext.All
            .Where(context => !context.IsCollectible && requiredAssemblies.All(expected =>
                context.Assemblies.Any(assembly => string.Equals(assembly.GetName().Name, expected, StringComparison.OrdinalIgnoreCase))))
            .ToArray();

        if (mode == "--participant-graph-use-unscoped-gate")
        {
            if (matchingContexts.Length != 0)
                throw new InvalidOperationException("The rejected participant load unexpectedly created a package graph context.");
            await WriteJsonLineAsync(new RefusedIdleMessage("refused-idle", request.OperationId, Environment.ProcessId,
                activationGateEvaluated, matchingContexts.Length)).ConfigureAwait(false);
        }
        else
        {
            if (matchingContexts.Length != 1)
                throw new InvalidOperationException(
                    $"Expected one noncollectible package graph context containing {string.Join(" and ", requiredAssemblies)}, found {matchingContexts.Length}.");

            var context = matchingContexts[0];
            var contextIdentity = $"{context.Name ?? "unnamed"}:{RuntimeHelpers.GetHashCode(context):x8}";
            var assemblyNames = context.Assemblies.Select(static assembly => assembly.GetName().Name!)
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            await WriteJsonLineAsync(new LoadedIdleMessage("loaded-idle", request.OperationId, Environment.ProcessId,
                contextIdentity, context.Name, context.IsCollectible, assemblyNames,
                provider.GetRequiredService<PackageStoreCompositionSnapshot>().IsParticipant,
                didParticipantRepeatCompositionAcceptance, didActivationGateProbeLiveUse)).ConfigureAwait(false);
        }

        var input = await Console.In.ReadLineAsync().ConfigureAwait(false);
        if (input is null)
            return PrematureEofExitCode;
        GraphUseReleaseCommand? command;
        try
        {
            command = JsonSerializer.Deserialize<GraphUseReleaseCommand>(input, JsonOptions);
        }
        catch (JsonException)
        {
            await WriteDiagnosticAsync("Expected one graph-use release command.").ConfigureAwait(false);
            return InvalidProtocolExitCode;
        }

        if (command is null || command.Command != "release" || command.OperationId != request.OperationId)
        {
            await WriteDiagnosticAsync("The graph-use release command did not match the operation.").ConfigureAwait(false);
            return InvalidProtocolExitCode;
        }

        await WriteJsonLineAsync(new ExitingMessage("exiting", request.OperationId, Environment.ProcessId)).ConfigureAwait(false);
        return 0;
    }

    private static ServiceProvider BuildGraphUseProvider(
        GraphUseProcessRequest request,
        PackageStoreMemberDeclaration[] completeRoster)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(nuplane =>
        {
            nuplane.WithStateFile(request.StateFilePath);
            nuplane.UseCoordinatedPackageStore(completeRoster, allClientsParticipate: true);
            nuplane.AutoloadPackages(loading => loading.WithDefaultLoadMode(PackageLoadMode.HostIntegrated));
        });
        services.AddSingleton<IDesiredPackageSource>(new GraphUseDesiredSource(request.DesiredRequests));
        services.Configure<FeedResolutionOptions>(options =>
        {
            options.PackageInstallRoot = request.RootPath;
            options.Feeds.Add(new FeedDefinition("feed", new Uri("https://unused.invalid/v3/index.json")));
        });
        services.Configure<LockFileOptions>(options =>
        {
            options.Mode = LockFileMode.Enforce;
            options.Path = Path.Combine(Path.GetDirectoryName(request.StateFilePath)!, "nuplane.lock.json");
        });
        return services.BuildServiceProvider();
    }

    private sealed class UnscopedActivationGate : IPackageActivationGate
    {
        public bool WasEvaluated { get; private set; }

        public ValueTask<PackageActivationGateResult> EvaluateAsync(
            PackageActivationContext context,
            CancellationToken cancellationToken)
        {
            WasEvaluated = true;
            return ValueTask.FromResult(PackageActivationGateResult.Allow);
        }
    }

    private sealed class ParticipantSnapshotProbeGate(
        NuplaneStoreSnapshotAccess access,
        IPackageStoreAdmission admission) : IScopedPackageActivationGate
    {
        public bool CompletedLiveUseProbe { get; private set; }

        public async ValueTask<PackageActivationGateResult> EvaluateAsync(
            PackageActivationContext context,
            CancellationToken cancellationToken)
        {
            await using (var snapshot = await access.OpenActivePackageSnapshotAsync(cancellationToken).ConfigureAwait(false))
            {
                if (snapshot.Snapshot.Packages.Count == 0)
                    return PackageActivationGateResult.Block("the selected active snapshot became empty before activation");
            }

            if (context.GraphUseLeases.Count != 1)
                return PackageActivationGateResult.Block("the host-integrated package graph did not carry one exact native graph-use lease");

            var expected = context.GraphUseLeases[0].Snapshot;
            if (expected.State != PackageGraphUseSnapshotState.Committed)
                return PackageActivationGateResult.Block("the graph-use lease was not committed before activation");

            await using var operation = await admission.AcquireConfiguredRootOperationAsync(
                PackageStoreAdmissionKind.Maintenance, cancellationToken).ConfigureAwait(false);
            var borrow = operation.Owner?.Borrow()
                ?? throw new InvalidOperationException("The participant gate probe received no root operation owner.");
            try
            {
                var observed = await PackageStoreOperationAccess.WithValidatedRootAsync(borrow,
                    async (files, root, token) =>
                    {
                        var members = PackageStoreOperationAccess.GetLockedMemberLocations(borrow);
                        return await new PackageGraphUseRecordStore(files)
                            .InspectAsync(root, borrow.Root, members.Ledger, token).ConfigureAwait(false);
                    }, cancellationToken).ConfigureAwait(false);
                if (observed.Entries.Count != 1 ||
                    observed.Entries[0].OwnershipState != GraphUseRecordOwnershipState.Live ||
                    !SameSnapshot(observed.Entries[0].Record.GraphSnapshot, expected))
                {
                    return PackageActivationGateResult.Block("the published graph-use record was not live and identical at activation");
                }
            }
            finally
            {
                borrow.Dispose();
            }

            CompletedLiveUseProbe = true;
            return PackageActivationGateResult.Allow;
        }

        private static bool SameSnapshot(ProtectedGraphSnapshot observed, PackageGraphUseSnapshot expected) =>
            observed.SnapshotId == expected.SnapshotId &&
            string.Equals(observed.GraphId, expected.GraphId, StringComparison.Ordinal) &&
            string.Equals(observed.GenerationId, expected.GenerationId, StringComparison.Ordinal) &&
            observed.Disposition == ProtectedGraphDisposition.Active &&
            observed.Roots.SequenceEqual(expected.Roots) &&
            observed.RequestedRoots.SequenceEqual(expected.RequestedRoots) &&
            observed.Nodes.SequenceEqual(expected.Nodes) &&
            observed.Edges.SequenceEqual(expected.Edges);
    }

    private static async Task<GraphUseProcessRequest> ReadGraphUseRequestAsync(string path)
    {
        var bytes = await ReadRequestBytesAsync(path).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<GraphUseProcessRequest>(bytes, JsonOptions)
            ?? throw new InvalidDataException("The graph-use process request was empty.");
        if (!Guid.TryParseExact(request.OperationId, "N", out _) ||
            string.IsNullOrWhiteSpace(request.RootPath) || !Path.IsPathFullyQualified(request.RootPath) ||
            string.IsNullOrWhiteSpace(request.StateFilePath) || !Path.IsPathFullyQualified(request.StateFilePath) ||
            request.CompleteRoster is null || request.CompleteRoster.Length is < 1 or > 8 ||
            request.DesiredRequests is null || request.DesiredRequests.Length is < 1 or > 8 ||
            request.DesiredRequests.Any(static desired => desired is null ||
                string.IsNullOrWhiteSpace(desired.Id) || string.IsNullOrWhiteSpace(desired.VersionRange) ||
                string.IsNullOrWhiteSpace(desired.FeedName) || string.IsNullOrWhiteSpace(desired.SourceName)))
        {
            throw new InvalidDataException("The graph-use process request fields were invalid.");
        }

        return request;
    }

    private sealed record GraphUseProcessRequest(
        string OperationId,
        string RootPath,
        string StateFilePath,
        PackageRequest[] DesiredRequests,
        PackageStoreMemberDeclaration[] CompleteRoster);

    private sealed record GraphUseReleaseCommand(string? Command, string? OperationId);
    private sealed record LoadedIdleMessage(
        string Kind,
        string OperationId,
        int ProcessId,
        string ContextIdentity,
        string? ContextName,
        bool IsCollectible,
        string[] AssemblyNames,
        bool IsCoordinatedParticipant,
        bool DidParticipantRepeatCompositionAcceptance = false,
        bool DidActivationGateProbeLiveUse = false);
    private sealed record RefusedIdleMessage(
        string Kind,
        string OperationId,
        int ProcessId,
        bool ActivationGateEvaluated,
        int MatchingPackageGraphContexts);
    private sealed record ExitingMessage(string Kind, string OperationId, int ProcessId);

    private sealed class GraphUseDesiredSource(IReadOnlyList<PackageRequest> requests) : IPackagePathIndependentDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(requests);
        }
    }
}

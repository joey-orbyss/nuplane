using System.Runtime.ExceptionServices;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.DeletionRecords;
using Nuplane.Store.Coordination.DeletionSerialization;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Publishes one first-phase Prepared journal through the existing native control-file publisher.</summary>
/// <remarks>
/// The journal is descriptive data. This store accepts only a live execution caller's callbacks for fresh
/// candidate validation and returns readback evidence only after reopening the exact canonical bytes.
/// It never moves or removes package content and does not recover pre-existing stages.
/// </remarks>
internal sealed class PackageStoreDeletionJournalStore
{
    internal const int MaximumControlEntries = 4096;
    internal const int MaximumPruneArtifacts = 1024;
    internal const long MaximumAggregatePruneBytes = 64L * 1024 * 1024;

    private readonly IPhysicalStoreFileSystem _files;
    private readonly IPhysicalStoreNameFileSystem _names;
    private readonly IPhysicalStoreDirectoryNameFileSystem _directories;
    private readonly IPhysicalStoreDirectoryEnumerationFileSystem _enumeration;
    private readonly IPhysicalStorePublicationFileSystem _publication;
    private readonly PackageStoreDeletionJournalSerializer _serializer = new();
    private readonly Action<PackageStoreDeletionJournalPublicationPoint, PackageStoreDeletionJournal>? _checkpoint;

    internal PackageStoreDeletionJournalStore(
        IPhysicalStoreFileSystem files,
        Action<PackageStoreDeletionJournalPublicationPoint, PackageStoreDeletionJournal>? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        _files = files;
        _names = files as IPhysicalStoreNameFileSystem
            ?? throw Unsupported("Deletion journal publication requires native file-name observations.");
        _directories = files as IPhysicalStoreDirectoryNameFileSystem
            ?? throw Unsupported("Deletion journal publication requires native directory-name observations.");
        _enumeration = files as IPhysicalStoreDirectoryEnumerationFileSystem
            ?? throw Unsupported("Deletion journal publication requires bounded native enumeration.");
        _publication = files as IPhysicalStorePublicationFileSystem
            ?? throw Unsupported("Deletion journal publication requires native no-replace control publication.");
        _checkpoint = checkpoint;
    }

    internal PublishedJournal PublishPrepared(
        PhysicalStoreDirectoryHandle heldRoot,
        PhysicalRootIdentity expectedRoot,
        PackageStoreDeletionJournal journal,
        Action<CancellationToken> requireCurrentCandidate,
        Action<CancellationToken> requireCurrentBaseline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(heldRoot);
        ArgumentNullException.ThrowIfNull(expectedRoot);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(requireCurrentCandidate);
        ArgumentNullException.ThrowIfNull(requireCurrentBaseline);
        if (journal.Phase != PackageStoreDeletionPhase.Prepared || journal.StoreRoot != expectedRoot)
            throw Refuse("Prepared publication requires a journal for the exact held root.", expectedRoot);

        cancellationToken.ThrowIfCancellationRequested();
        requireCurrentCandidate(cancellationToken);
        requireCurrentBaseline(cancellationToken);
        var payload = _serializer.Serialize(journal);
        if (payload.Length > PackageStoreDeletionJournalSerializer.MaximumPayloadBytes)
            throw Refuse("The Prepared journal exceeds the per-record payload limit.", expectedRoot);

        var canonicalName = CanonicalName(journal.CandidateId);
        var publicationId = NewDistinctId(journal.OperationId, journal.CandidateId);
        var stagedName = StagedName(journal.CandidateId, publicationId);

        var rootSemantics = VerifyRoot(heldRoot, expectedRoot);
        using var control = OpenAndVerifyControl(heldRoot, expectedRoot, rootSemantics,
            out var controlIdentity, out var controlSemantics);

        var existingNames = _enumeration.EnumerateChildNamesNoFollow(control, MaximumControlEntries);
        if (existingNames.Count + 1 > MaximumControlEntries)
            throw Refuse("Publishing the stage would exceed the complete control-directory enumeration limit.", expectedRoot);

        var artifacts = InspectPruneArtifacts(control, controlIdentity, controlSemantics,
            existingNames, expectedRoot);
        if (artifacts.Count + 1 > MaximumPruneArtifacts)
            throw Refuse("Publishing the stage would exceed the reserved prune-artifact count limit.", expectedRoot);
        var aggregateBytes = artifacts.Aggregate(0L, static (sum, item) => checked(sum + item.Info.Length));
        if (checked(aggregateBytes + payload.Length) > MaximumAggregatePruneBytes)
            throw Refuse("Publishing the stage would exceed the aggregate prune-record byte limit.", expectedRoot);

        if (artifacts.Any(static item => item.IsStage))
            throw Refuse("An existing prune publication stage is unresolved; it is preserved for explicit recovery.", expectedRoot);

        ValidateExistingJournals(control, controlIdentity, controlSemantics, artifacts, expectedRoot, journal);
        if (existingNames.Contains(canonicalName, StringComparer.Ordinal) ||
            existingNames.Contains(stagedName, StringComparer.Ordinal))
        {
            throw Refuse("The candidate's canonical journal or exact publication stage already exists.", expectedRoot);
        }

        cancellationToken.ThrowIfCancellationRequested();
        requireCurrentCandidate(cancellationToken);
        requireCurrentBaseline(cancellationToken);
        VerifyRootAndControl(heldRoot, expectedRoot, rootSemantics, control, controlIdentity, controlSemantics);

        PhysicalStorePublicationChecks.RequireExpectedEntry(
            _files.InspectChildNoFollow(control, canonicalName), expectedIdentity: null);
        PhysicalStorePublicationChecks.RequireExpectedEntry(
            _files.InspectChildNoFollow(control, stagedName), expectedIdentity: null);

        var stagedIdentity = CreateFlushedStage(control, stagedName, payload, expectedRoot.HandleIdentity);
        _checkpoint?.Invoke(PackageStoreDeletionJournalPublicationPoint.StageFlushed, journal);
        VerifyCanonicalFile(control, controlIdentity, controlSemantics, stagedName, stagedIdentity,
            expectedRoot.HandleIdentity, payload.Length);
        var stagedBytes = ReadExactFile(control, controlIdentity, controlSemantics, stagedName,
            stagedIdentity, expectedRoot.HandleIdentity, PackageStoreDeletionJournalSerializer.MaximumPayloadBytes);
        RequireExactPayload(stagedBytes, payload, journal, expectedRoot);

        cancellationToken.ThrowIfCancellationRequested();
        requireCurrentCandidate(cancellationToken);
        requireCurrentBaseline(cancellationToken);
        VerifyRootAndControl(heldRoot, expectedRoot, rootSemantics, control, controlIdentity, controlSemantics);
        var prepared = PhysicalStorePublicationChecks.PreparePublish(_files, _names, control,
            stagedName, stagedIdentity, canonicalName, destinationIdentity: null);
        if (prepared.ParentIdentity != controlIdentity || prepared.Semantics != controlSemantics)
            throw Refuse("The Prepared stage does not bind the expected native control-directory profile.", expectedRoot);

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _checkpoint?.Invoke(PackageStoreDeletionJournalPublicationPoint.BeforeNativePublish, journal);
            _publication.PublishControlFileAt(control, stagedName, stagedIdentity, canonicalName,
                expectedDestinationIdentity: null);
            _checkpoint?.Invoke(PackageStoreDeletionJournalPublicationPoint.AfterNativePublish, journal);
            VerifyPublishedInControl(heldRoot, expectedRoot, rootSemantics,
                control, controlIdentity, controlSemantics, canonicalName, stagedName,
                stagedIdentity, payload, journal);
            return new PublishedJournal(journal, canonicalName, stagedName, stagedIdentity, payload, payload.Length,
                rootSemantics, controlIdentity, controlSemantics);
        }
        catch (Exception publicationFailure)
        {
            try
            {
                VerifyPublishedInControl(heldRoot, expectedRoot, rootSemantics,
                    control, controlIdentity, controlSemantics, canonicalName, stagedName,
                    stagedIdentity, payload, journal);
                return new PublishedJournal(journal, canonicalName, stagedName, stagedIdentity, payload, payload.Length,
                    rootSemantics, controlIdentity, controlSemantics);
            }
            catch (Exception publishedStateFailure)
            {
                try
                {
                    VerifyUnpublishedStageInControl(heldRoot, expectedRoot, rootSemantics,
                        control, controlIdentity, controlSemantics, canonicalName, stagedName,
                        stagedIdentity, payload, journal);
                }
                catch (Exception stageStateFailure)
                {
                    throw Refuse("Prepared publication ended in an unknown native state; all observed artifacts were preserved.",
                        expectedRoot,
                        new AggregateException("Publication and both exact state inspections failed.",
                            publicationFailure, publishedStateFailure, stageStateFailure));
                }

                ExceptionDispatchInfo.Capture(publicationFailure).Throw();
                throw new InvalidOperationException("Unreachable after rethrowing a preserved publication failure.");
            }
        }
    }

    internal void VerifyPublished(
        PhysicalStoreDirectoryHandle heldRoot,
        PhysicalRootIdentity expectedRoot,
        PublishedJournal published,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(published);
        cancellationToken.ThrowIfCancellationRequested();
        var rootSemantics = VerifyRoot(heldRoot, expectedRoot);
        if (rootSemantics != published.RootSemantics)
            throw Refuse("The held store root's native name profile changed after Prepared publication.", expectedRoot);
        using var control = OpenAndVerifyControl(heldRoot, expectedRoot, rootSemantics,
            out var controlIdentity, out var controlSemantics);
        if (controlIdentity != published.ControlDirectoryIdentity || controlSemantics != published.ControlDirectorySemantics)
            throw Refuse("The reserved control directory identity or native profile changed after Prepared publication.", expectedRoot);
        VerifyPublishedInControl(heldRoot, expectedRoot, rootSemantics, control, controlIdentity,
            controlSemantics, published.CanonicalName, published.StagedName,
            published.FileIdentity, published.Payload, published.Journal);
    }

    private List<PruneArtifact> InspectPruneArtifacts(
        PhysicalStoreDirectoryHandle control,
        PhysicalFileIdentity controlIdentity,
        PhysicalStoreNameSemantics controlSemantics,
        IReadOnlyList<string> names,
        PhysicalRootIdentity expectedRoot)
    {
        var reservedCount = names.Count(static name => name.StartsWith("prune", StringComparison.OrdinalIgnoreCase));
        if (reservedCount > MaximumPruneArtifacts)
            throw Refuse("The reserved prune-artifact namespace exceeds its name-count limit.", expectedRoot);

        var artifacts = new List<PruneArtifact>(reservedCount);
        long totalBytes = 0;
        foreach (var name in names)
        {
            if (!name.StartsWith("prune", StringComparison.OrdinalIgnoreCase))
                continue;

            var parsed = ParseArtifactName(name, expectedRoot);
            try { PhysicalStoreDeletionManifestEntry.ValidateComponent(name, controlSemantics); }
            catch (ArgumentException exception)
            {
                throw Refuse("A reserved prune artifact name is invalid under the native control-directory profile.",
                    expectedRoot, exception);
            }
            var entry = _files.InspectChildNoFollow(control, name)
                ?? throw Refuse("A reserved prune artifact disappeared during native inspection.", expectedRoot);
            if (entry.Kind != PhysicalStoreEntryKind.RegularFile || entry.LinkCount != 1 ||
                entry.ReparseTag is not (null or 0) || entry.Length < 0 ||
                entry.Length > PackageStoreDeletionJournalSerializer.MaximumPayloadBytes ||
                !SameVolume(entry.Identity, expectedRoot.HandleIdentity))
            {
                throw Refuse("A reserved prune artifact is not a bounded regular single-link control file.", expectedRoot);
            }

            var canonical = PhysicalStorePublicationChecks.PrepareRemoval(_files, _names, control, name, entry.Identity);
            if (canonical.ParentIdentity != controlIdentity || canonical.Semantics != controlSemantics ||
                !string.Equals(canonical.Basename, name, StringComparison.Ordinal))
            {
                throw Refuse("A reserved prune artifact is not at its exact canonical native basename.", expectedRoot);
            }

            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > MaximumAggregatePruneBytes)
                throw Refuse("Existing prune artifacts exceed their aggregate byte limit.", expectedRoot);
            artifacts.Add(new PruneArtifact(name, parsed.CandidateId, parsed.IsStage, entry));
        }

        return artifacts;
    }

    private void ValidateExistingJournals(
        PhysicalStoreDirectoryHandle control,
        PhysicalFileIdentity controlIdentity,
        PhysicalStoreNameSemantics controlSemantics,
        IReadOnlyList<PruneArtifact> artifacts,
        PhysicalRootIdentity expectedRoot,
        PackageStoreDeletionJournal requested)
    {
        foreach (var artifact in artifacts)
        {
            if (artifact.IsStage)
                continue;

            var bytes = ReadExactFile(control, controlIdentity, controlSemantics, artifact.Name,
                artifact.Info.Identity, expectedRoot.HandleIdentity,
                PackageStoreDeletionJournalSerializer.MaximumPayloadBytes);
            PackageStoreDeletionJournal existing;
            try { existing = _serializer.Deserialize(bytes); }
            catch (System.Text.Json.JsonException exception)
            {
                throw Refuse("A reserved canonical prune journal is malformed and was preserved.", expectedRoot, exception);
            }

            if (existing.CandidateId != artifact.CandidateId ||
                existing.Phase != PackageStoreDeletionPhase.Prepared ||
                existing.StoreRoot != expectedRoot || existing.EnrollmentEpoch != requested.EnrollmentEpoch)
            {
                throw Refuse("An existing prune journal is unknown to this first-phase publisher and was preserved.", expectedRoot);
            }

            if (existing.CandidateId == requested.CandidateId ||
                existing.OriginalInstall.DirectoryIdentity == requested.OriginalInstall.DirectoryIdentity ||
                existing.PackageParentIdentity == requested.PackageParentIdentity &&
                string.Equals(existing.OriginalBasename, requested.OriginalBasename, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(existing.OriginalInstall.RootRelativeInstallPath,
                    requested.OriginalInstall.RootRelativeInstallPath, StringComparison.OrdinalIgnoreCase))
            {
                throw Refuse("An existing Prepared record already describes this exact or possibly aliased install slot.", expectedRoot);
            }
        }
    }

    private PhysicalStoreDirectoryHandle OpenAndVerifyControl(
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity expectedRoot,
        PhysicalStoreNameSemantics rootSemantics,
        out PhysicalFileIdentity controlIdentity,
        out PhysicalStoreNameSemantics controlSemantics)
    {
        var entry = _files.InspectChildNoFollow(root, RootMembershipRegistry.ControlDirectoryName);
        if (entry is null || entry.Kind != PhysicalStoreEntryKind.Directory ||
            entry.ReparseTag is not (null or 0) || !SameVolume(entry.Identity, expectedRoot.HandleIdentity))
            throw Refuse("The reserved control directory is missing or does not share the held root's native volume.", expectedRoot);

        var canonical = _directories.ObserveCanonicalDirectoryNameNoFollow(
            root, RootMembershipRegistry.ControlDirectoryName, entry.Identity);
        if (canonical.ParentIdentity != expectedRoot.HandleIdentity || canonical.FileIdentity != entry.Identity ||
            canonical.Semantics != rootSemantics ||
            !string.Equals(canonical.Basename, RootMembershipRegistry.ControlDirectoryName, StringComparison.Ordinal))
            throw Refuse("The reserved control directory is not the exact canonical child of the held root.", expectedRoot);

        var control = _files.OpenDirectoryChildNoFollow(root, RootMembershipRegistry.ControlDirectoryName);
        try
        {
            var info = _files.InspectHandle(control);
            if (info.Kind != PhysicalStoreEntryKind.Directory || info.Identity != entry.Identity ||
                info.ReparseTag is not (null or 0))
                throw Refuse("The opened control directory differs from its no-follow native observation.", expectedRoot);
            controlIdentity = info.Identity;
            controlSemantics = _names.ObserveDirectoryNameSemantics(control);
            VerifyRootAndControl(root, expectedRoot, rootSemantics, control, controlIdentity, controlSemantics);
            return control;
        }
        catch (Exception primaryFailure)
        {
            try { control.Dispose(); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Opening the control directory failed and its handle could not be closed.",
                    primaryFailure, cleanupFailure);
            }
            throw;
        }
    }

    private void VerifyRootAndControl(
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity expectedRoot,
        PhysicalStoreNameSemantics expectedRootSemantics,
        PhysicalStoreDirectoryHandle control,
        PhysicalFileIdentity expectedControlIdentity,
        PhysicalStoreNameSemantics expectedControlSemantics)
    {
        var rootInfo = _files.InspectHandle(root);
        if (rootInfo.Kind != PhysicalStoreEntryKind.Directory || rootInfo.Identity != expectedRoot.HandleIdentity ||
            _names.ObserveDirectoryNameSemantics(root) != expectedRootSemantics)
            throw Refuse("The held store root identity or native name profile changed.", expectedRoot);

        var controlInfo = _files.InspectHandle(control);
        var namedControl = _files.InspectChildNoFollow(root, RootMembershipRegistry.ControlDirectoryName);
        var canonical = _directories.ObserveCanonicalDirectoryNameNoFollow(
            root, RootMembershipRegistry.ControlDirectoryName, expectedControlIdentity);
        if (controlInfo.Kind != PhysicalStoreEntryKind.Directory || controlInfo.Identity != expectedControlIdentity ||
            namedControl is null || namedControl.Kind != PhysicalStoreEntryKind.Directory ||
            namedControl.Identity != expectedControlIdentity || !SameVolume(expectedControlIdentity, expectedRoot.HandleIdentity) ||
            canonical.ParentIdentity != expectedRoot.HandleIdentity || canonical.FileIdentity != expectedControlIdentity ||
            canonical.Semantics != expectedRootSemantics ||
            !string.Equals(canonical.Basename, RootMembershipRegistry.ControlDirectoryName, StringComparison.Ordinal) ||
            _names.ObserveDirectoryNameSemantics(control) != expectedControlSemantics)
            throw Refuse("The held control directory no longer binds the exact root and native profile.", expectedRoot);
    }

    private PhysicalStoreNameSemantics VerifyRoot(PhysicalStoreDirectoryHandle root, PhysicalRootIdentity expectedRoot)
    {
        var info = _files.InspectHandle(root);
        if (info.Kind != PhysicalStoreEntryKind.Directory || info.Identity != expectedRoot.HandleIdentity)
            throw Refuse("The supplied held directory does not match the expected physical store root.", expectedRoot);
        return _names.ObserveDirectoryNameSemantics(root);
    }

    private PhysicalFileIdentity CreateFlushedStage(
        PhysicalStoreDirectoryHandle control,
        string stagedName,
        byte[] payload,
        PhysicalFileIdentity rootIdentity)
    {
        var staged = _files.CreateFileExclusiveAt(control, stagedName);
        PhysicalFileIdentity identity;
        try
        {
            _files.WriteNewControlFile(staged, payload);
            var info = _files.InspectHandle(staged);
            RequireControlFile(info, expectedIdentity: null, rootIdentity, payload.Length,
                "The flushed Prepared stage is not a regular single-link file on the held root's volume.");
            identity = info.Identity;
        }
        catch (Exception primaryFailure)
        {
            try { staged.Dispose(); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Writing the Prepared stage failed and its creation handle could not be closed.",
                    primaryFailure, cleanupFailure);
            }
            throw;
        }

        staged.Dispose();
        return identity;
    }

    private void VerifyPublishedInControl(
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity expectedRoot,
        PhysicalStoreNameSemantics rootSemantics,
        PhysicalStoreDirectoryHandle control,
        PhysicalFileIdentity controlIdentity,
        PhysicalStoreNameSemantics controlSemantics,
        string canonicalName,
        string stagedName,
        PhysicalFileIdentity stagedIdentity,
        byte[] payload,
        PackageStoreDeletionJournal journal)
    {
        VerifyRootAndControl(root, expectedRoot, rootSemantics, control, controlIdentity, controlSemantics);
        var prepared = new PhysicalStoreCanonicalName(controlIdentity, stagedIdentity, stagedName, controlSemantics);
        var published = PhysicalStorePublicationChecks.VerifyPublished(
            _files, _names, control, stagedName, canonicalName, prepared);
        RequireControlFile(published, stagedIdentity, expectedRoot.HandleIdentity, payload.Length,
            "The canonical Prepared journal does not match the flushed stage identity and length.");
        var bytes = ReadExactFile(control, controlIdentity, controlSemantics, canonicalName,
            stagedIdentity, expectedRoot.HandleIdentity, PackageStoreDeletionJournalSerializer.MaximumPayloadBytes);
        RequireExactPayload(bytes, payload, journal, expectedRoot);
        VerifyRootAndControl(root, expectedRoot, rootSemantics, control, controlIdentity, controlSemantics);
    }

    private void VerifyUnpublishedStageInControl(
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity expectedRoot,
        PhysicalStoreNameSemantics rootSemantics,
        PhysicalStoreDirectoryHandle control,
        PhysicalFileIdentity controlIdentity,
        PhysicalStoreNameSemantics controlSemantics,
        string canonicalName,
        string stagedName,
        PhysicalFileIdentity stagedIdentity,
        byte[] payload,
        PackageStoreDeletionJournal journal)
    {
        VerifyRootAndControl(root, expectedRoot, rootSemantics, control, controlIdentity, controlSemantics);
        PhysicalStorePublicationChecks.RequireExpectedEntry(
            _files.InspectChildNoFollow(control, canonicalName), expectedIdentity: null);
        VerifyCanonicalFile(control, controlIdentity, controlSemantics, stagedName, stagedIdentity,
            expectedRoot.HandleIdentity, payload.Length);
        var bytes = ReadExactFile(control, controlIdentity, controlSemantics, stagedName,
            stagedIdentity, expectedRoot.HandleIdentity, PackageStoreDeletionJournalSerializer.MaximumPayloadBytes);
        RequireExactPayload(bytes, payload, journal, expectedRoot);
        VerifyRootAndControl(root, expectedRoot, rootSemantics, control, controlIdentity, controlSemantics);
    }

    private void VerifyCanonicalFile(
        PhysicalStoreDirectoryHandle parent,
        PhysicalFileIdentity parentIdentity,
        PhysicalStoreNameSemantics semantics,
        string name,
        PhysicalFileIdentity expectedIdentity,
        PhysicalFileIdentity rootIdentity,
        long expectedLength)
    {
        var canonical = PhysicalStorePublicationChecks.PrepareRemoval(_files, _names, parent, name, expectedIdentity);
        if (canonical.ParentIdentity != parentIdentity || canonical.Semantics != semantics ||
            !string.Equals(canonical.Basename, name, StringComparison.Ordinal))
            throw Refuse("A deletion journal control file is not at its exact canonical native name.");
        var entry = PhysicalStorePublicationChecks.RequireExpectedEntry(
            _files.InspectChildNoFollow(parent, name), expectedIdentity)!;
        RequireControlFile(entry, expectedIdentity, rootIdentity, expectedLength,
            "A deletion journal control file has the wrong identity, type, link count, length or volume.");
    }

    private byte[] ReadExactFile(
        PhysicalStoreDirectoryHandle parent,
        PhysicalFileIdentity parentIdentity,
        PhysicalStoreNameSemantics semantics,
        string name,
        PhysicalFileIdentity expectedIdentity,
        PhysicalFileIdentity rootIdentity,
        int maximumBytes)
    {
        var before = PhysicalStorePublicationChecks.RequireExpectedEntry(
            _files.InspectChildNoFollow(parent, name), expectedIdentity)!;
        RequireControlFile(before, expectedIdentity, rootIdentity, before.Length,
            "A deletion journal file changed before its bounded read.");
        VerifyCanonicalFile(parent, parentIdentity, semantics, name, expectedIdentity, rootIdentity, before.Length);

        var opened = _files.OpenFileChildNoFollow(parent, name, FileAccess.Read);
        byte[] bytes;
        try
        {
            var info = _files.InspectHandle(opened);
            RequireControlFile(info, expectedIdentity, rootIdentity, before.Length,
                "The reopened deletion journal changed identity, type, link count, volume or length.");
            bytes = _files.ReadControlFile(opened, maximumBytes);
            var after = _files.InspectHandle(opened);
            RequireControlFile(after, expectedIdentity, rootIdentity, bytes.Length,
                "The deletion journal changed during its bounded read.");
            if (bytes.Length != before.Length)
                throw Refuse("The deletion journal length changed during its bounded read.");
        }
        catch (Exception primaryFailure)
        {
            try { opened.Dispose(); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Reading a deletion journal failed and its temporary handle could not be closed.",
                    primaryFailure, cleanupFailure);
            }
            throw;
        }
        opened.Dispose();
        VerifyCanonicalFile(parent, parentIdentity, semantics, name, expectedIdentity, rootIdentity, bytes.Length);
        return bytes;
    }

    private void RequireExactPayload(byte[] actual, byte[] expected, PackageStoreDeletionJournal journal,
        PhysicalRootIdentity expectedRoot)
    {
        if (!actual.AsSpan().SequenceEqual(expected))
            throw Refuse("The reopened deletion journal differs from the exact flushed candidate bytes.", expectedRoot);
        var restored = _serializer.Deserialize(actual);
        if (restored.IntegrityDigest != journal.IntegrityDigest || restored.CandidateId != journal.CandidateId ||
            restored.OperationId != journal.OperationId || restored.Phase != PackageStoreDeletionPhase.Prepared ||
            restored.StoreRoot != expectedRoot || restored.Manifest.InstallDirectoryIdentity != journal.Manifest.InstallDirectoryIdentity)
            throw Refuse("The reopened deletion journal does not bind the exact Prepared candidate.", expectedRoot);
    }

    private static PruneArtifactName ParseArtifactName(string name, PhysicalRootIdentity root)
    {
        const string prefix = "prune-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
            throw Refuse("A malformed or aliased name occupies the reserved prune namespace.", root);

        var stageSeparator = name.IndexOf(".stage-", StringComparison.Ordinal);
        if (stageSeparator >= 0)
        {
            var canonical = name[..stageSeparator];
            var publicationText = name[(stageSeparator + ".stage-".Length)..];
            var candidateId = ParseCanonicalName(canonical, root);
            var publicationId = ParseGuidN(publicationText, root);
            if (publicationId == Guid.Empty || publicationId == candidateId)
                throw Refuse("A prune stage has an invalid or non-distinct publication ID.", root);
            return new PruneArtifactName(candidateId, IsStage: true);
        }

        return new PruneArtifactName(ParseCanonicalName(name, root), IsStage: false);
    }

    private static Guid ParseCanonicalName(string name, PhysicalRootIdentity root)
    {
        const string prefix = "prune-";
        const string suffix = ".json";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal) ||
            name.Length != prefix.Length + 32 + suffix.Length)
            throw Refuse("A malformed or aliased name occupies the reserved prune namespace.", root);
        return ParseGuidN(name.Substring(prefix.Length, 32), root);
    }

    private static Guid ParseGuidN(string value, PhysicalRootIdentity root)
    {
        if (value.Length != 32 || value.Any(static character =>
                !(character is >= '0' and <= '9' or >= 'a' and <= 'f')) ||
            !Guid.TryParseExact(value, "N", out var parsed) ||
            !string.Equals(parsed.ToString("N"), value, StringComparison.Ordinal))
            throw Refuse("A reserved prune artifact has a noncanonical GUID component.", root);
        return parsed;
    }

    private static string CanonicalName(Guid candidateId) => $"prune-{candidateId:N}.json";

    private static string StagedName(Guid candidateId, Guid publicationId)
        => $"{CanonicalName(candidateId)}.stage-{publicationId:N}";

    private static Guid NewDistinctId(Guid first, Guid second)
    {
        Guid id;
        do id = Guid.NewGuid(); while (id == Guid.Empty || id == first || id == second);
        return id;
    }

    private static void RequireControlFile(PhysicalStoreEntryInfo info, PhysicalFileIdentity? expectedIdentity,
        PhysicalFileIdentity rootIdentity, long expectedLength, string message)
    {
        if (info.Kind != PhysicalStoreEntryKind.RegularFile || info.LinkCount != 1 ||
            info.ReparseTag is not (null or 0) || (expectedIdentity is not null && info.Identity != expectedIdentity) ||
            info.Length != expectedLength || !SameVolume(info.Identity, rootIdentity))
            throw Refuse(message);
    }

    private static bool SameVolume(PhysicalFileIdentity left, PhysicalFileIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.Ordinal) &&
           string.Equals(left.VolumeOrDeviceId, right.VolumeOrDeviceId, StringComparison.Ordinal);

    private static PackageStoreAdmissionException Unsupported(string message)
        => new(PackageStoreAdmissionReason.UnsupportedFilesystem, message);

    private static PackageStoreAdmissionException Refuse(string message, PhysicalRootIdentity? root = null,
        Exception? innerException = null)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message, root, innerException);

    internal sealed record PublishedJournal(
        PackageStoreDeletionJournal Journal,
        string CanonicalName,
        string StagedName,
        PhysicalFileIdentity FileIdentity,
        byte[] Payload,
        long Length,
        PhysicalStoreNameSemantics RootSemantics,
        PhysicalFileIdentity ControlDirectoryIdentity,
        PhysicalStoreNameSemantics ControlDirectorySemantics);

    private sealed record PruneArtifact(string Name, Guid CandidateId, bool IsStage, PhysicalStoreEntryInfo Info);
    private readonly record struct PruneArtifactName(Guid CandidateId, bool IsStage);
}

internal enum PackageStoreDeletionJournalPublicationPoint
{
    StageFlushed,
    BeforeNativePublish,
    AfterNativePublish
}

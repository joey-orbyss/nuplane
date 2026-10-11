using System.Text.Json;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.DeletionRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State.ProtectionSerialization;

namespace Nuplane.Store.Coordination.DeletionSerialization;

internal sealed class PackageStoreDeletionJournalDto
{
    public int? SchemaVersion { get; set; }
    public PhysicalRootIdentityDto? StoreRoot { get; set; }
    public long? EnrollmentEpoch { get; set; }
    public Guid? OperationId { get; set; }
    public Guid? CandidateId { get; set; }
    public PackageInstallIdentityDto? OriginalInstall { get; set; }
    public List<string?>? PackageParentRelativeComponents { get; set; }
    public PhysicalFileIdentityDto? PackageParentIdentity { get; set; }
    public PhysicalStoreNameSemanticsDto? PackageParentSemantics { get; set; }
    public string? OriginalBasename { get; set; }
    public string? QuarantineBasename { get; set; }
    public PhysicalFileIdentityDto? CompletionMarkerIdentity { get; set; }
    public int? KeepNewestInactiveVersionsPerPackage { get; set; }
    public int? Phase { get; set; }
    public PhysicalStoreDeletionManifestDto? Manifest { get; set; }
    public string? IntegrityDigest { get; set; }

    internal PackageStoreDeletionJournal ToJournal()
    {
        if (SchemaVersion != PackageStoreDeletionJournal.CurrentSchemaVersion)
            throw new JsonException("The deletion-journal schema version is unsupported.");

        var digest = Required(IntegrityDigest, nameof(IntegrityDigest));
        ProtectionDigest.ValidateCanonicalDigest(digest);
        var phase = ReadEnum<PackageStoreDeletionPhase>(Phase, nameof(Phase));
        var parentComponents = MapRequired(
            PackageParentRelativeComponents,
            nameof(PackageParentRelativeComponents),
            static component => component ?? throw new JsonException("The package-parent locator cannot contain null components."));

        return new PackageStoreDeletionJournal(
            SchemaVersion.Value,
            Required(StoreRoot, nameof(StoreRoot)).ToIdentity(),
            Required(EnrollmentEpoch, nameof(EnrollmentEpoch)),
            Required(OperationId, nameof(OperationId)),
            Required(CandidateId, nameof(CandidateId)),
            Required(OriginalInstall, nameof(OriginalInstall)).ToInstallIdentity(),
            parentComponents,
            Required(PackageParentIdentity, nameof(PackageParentIdentity)).ToIdentity(),
            Required(PackageParentSemantics, nameof(PackageParentSemantics)).ToSemantics(),
            Required(OriginalBasename, nameof(OriginalBasename)),
            Required(QuarantineBasename, nameof(QuarantineBasename)),
            Required(CompletionMarkerIdentity, nameof(CompletionMarkerIdentity)).ToIdentity(),
            Required(KeepNewestInactiveVersionsPerPackage, nameof(KeepNewestInactiveVersionsPerPackage)),
            phase,
            Required(Manifest, nameof(Manifest)).ToManifest(),
            digest);
    }

    internal static PackageStoreDeletionJournalDto FromJournal(PackageStoreDeletionJournal journal)
        => new()
        {
            SchemaVersion = journal.SchemaVersion,
            StoreRoot = PhysicalRootIdentityDto.FromIdentity(journal.StoreRoot),
            EnrollmentEpoch = journal.EnrollmentEpoch,
            OperationId = journal.OperationId,
            CandidateId = journal.CandidateId,
            OriginalInstall = PackageInstallIdentityDto.FromInstallIdentity(journal.OriginalInstall),
            PackageParentRelativeComponents = journal.PackageParentRelativeComponents.Cast<string?>().ToList(),
            PackageParentIdentity = PhysicalFileIdentityDto.FromIdentity(journal.PackageParentIdentity),
            PackageParentSemantics = PhysicalStoreNameSemanticsDto.FromSemantics(journal.PackageParentSemantics),
            OriginalBasename = journal.OriginalBasename,
            QuarantineBasename = journal.QuarantineBasename,
            CompletionMarkerIdentity = PhysicalFileIdentityDto.FromIdentity(journal.CompletionMarkerIdentity),
            KeepNewestInactiveVersionsPerPackage = journal.KeepNewestInactiveVersionsPerPackage,
            Phase = (int)journal.Phase,
            Manifest = PhysicalStoreDeletionManifestDto.FromManifest(journal.Manifest),
            IntegrityDigest = journal.IntegrityDigest
        };

    private static T Required<T>(T? value, string name) where T : struct
        => value ?? throw Missing(name);

    private static T Required<T>(T? value, string name) where T : class
        => value ?? throw Missing(name);

    private static TEnum ReadEnum<TEnum>(int? raw, string name) where TEnum : struct, Enum
    {
        var value = raw ?? throw Missing(name);
        var parsed = (TEnum)Enum.ToObject(typeof(TEnum), value);
        return Enum.IsDefined(parsed) ? parsed : throw new JsonException($"The deletion journal has an unsupported '{name}'.");
    }

    private static List<TResult> MapRequired<TSource, TResult>(
        List<TSource?>? values,
        string name,
        Func<TSource, TResult> map)
        where TSource : class
    {
        if (values is null)
            throw Missing(name);
        var mapped = new List<TResult>(values.Count);
        foreach (var value in values)
        {
            if (value is null)
                throw new JsonException($"The deletion journal '{name}' array cannot contain null items.");
            mapped.Add(map(value));
        }
        return mapped;
    }

    private static JsonException Missing(string name) => new($"The deletion journal is missing '{name}'.");
}

internal sealed class PhysicalStoreDeletionManifestDto
{
    public PhysicalFileIdentityDto? InstallDirectoryIdentity { get; set; }
    public PhysicalStoreNameSemanticsDto? InstallDirectorySemantics { get; set; }
    public List<PhysicalStoreDeletionManifestEntryDto?>? Entries { get; set; }

    internal PhysicalStoreDeletionManifest ToManifest()
        => new(
            Required(InstallDirectoryIdentity, nameof(InstallDirectoryIdentity)).ToIdentity(),
            Required(InstallDirectorySemantics, nameof(InstallDirectorySemantics)).ToSemantics(),
            MapRequired(Entries, nameof(Entries), static entry => entry.ToEntry()));

    internal static PhysicalStoreDeletionManifestDto FromManifest(PhysicalStoreDeletionManifest manifest)
        => new()
        {
            InstallDirectoryIdentity = PhysicalFileIdentityDto.FromIdentity(manifest.InstallDirectoryIdentity),
            InstallDirectorySemantics = PhysicalStoreNameSemanticsDto.FromSemantics(manifest.InstallDirectorySemantics),
            Entries = manifest.Entries.Select(static entry => (PhysicalStoreDeletionManifestEntryDto?)PhysicalStoreDeletionManifestEntryDto.FromEntry(entry)).ToList()
        };

    private static T Required<T>(T? value, string name) where T : class
        => value ?? throw new JsonException($"The deletion manifest is missing '{name}'.");

    private static List<TResult> MapRequired<TSource, TResult>(
        List<TSource?>? values,
        string name,
        Func<TSource, TResult> map)
        where TSource : class
    {
        if (values is null)
            throw new JsonException($"The deletion manifest is missing '{name}'.");
        var mapped = new List<TResult>(values.Count);
        foreach (var value in values)
        {
            if (value is null)
                throw new JsonException($"The deletion manifest '{name}' array cannot contain null items.");
            mapped.Add(map(value));
        }
        return mapped;
    }
}

internal sealed class PhysicalStoreDeletionManifestEntryDto
{
    public List<string?>? RelativeComponents { get; set; }
    public int? Kind { get; set; }
    public PhysicalFileIdentityDto? Identity { get; set; }
    public PhysicalFileIdentityDto? ParentIdentity { get; set; }
    public PhysicalStoreNameSemanticsDto? ParentSemantics { get; set; }
    public PhysicalStoreNameSemanticsDto? DirectorySemantics { get; set; }

    internal PhysicalStoreDeletionManifestEntry ToEntry()
        => new(
            MapRequired(RelativeComponents, nameof(RelativeComponents), static component => component),
            ReadEntryKind(Kind),
            Required(Identity, nameof(Identity)).ToIdentity(),
            Required(ParentIdentity, nameof(ParentIdentity)).ToIdentity(),
            Required(ParentSemantics, nameof(ParentSemantics)).ToSemantics(),
            DirectorySemantics?.ToSemantics());

    internal static PhysicalStoreDeletionManifestEntryDto FromEntry(PhysicalStoreDeletionManifestEntry entry)
        => new()
        {
            RelativeComponents = entry.RelativeComponents.Cast<string?>().ToList(),
            Kind = (int)entry.Kind,
            Identity = PhysicalFileIdentityDto.FromIdentity(entry.Identity),
            ParentIdentity = PhysicalFileIdentityDto.FromIdentity(entry.ParentIdentity),
            ParentSemantics = PhysicalStoreNameSemanticsDto.FromSemantics(entry.ParentSemantics),
            DirectorySemantics = entry.DirectorySemantics is null
                ? null
                : PhysicalStoreNameSemanticsDto.FromSemantics(entry.DirectorySemantics)
        };

    private static PhysicalStoreEntryKind ReadEntryKind(int? raw)
    {
        var value = raw ?? throw new JsonException("A deletion manifest entry is missing its kind.");
        var kind = (PhysicalStoreEntryKind)Enum.ToObject(typeof(PhysicalStoreEntryKind), value);
        return kind is PhysicalStoreEntryKind.Directory or PhysicalStoreEntryKind.RegularFile
            ? kind
            : throw new JsonException("A deletion manifest entry has an unsupported kind.");
    }

    private static List<string> MapRequired(List<string?>? values, string name, Func<string, string> map)
    {
        if (values is null)
            throw new JsonException($"A deletion manifest entry is missing '{name}'.");
        var mapped = new List<string>(values.Count);
        foreach (var value in values)
        {
            if (value is null)
                throw new JsonException("A deletion manifest path cannot contain null components.");
            mapped.Add(map(value));
        }
        return mapped;
    }

    private static T Required<T>(T? value, string name) where T : class
        => value ?? throw new JsonException($"A deletion manifest entry is missing '{name}'.");
}

internal sealed class PhysicalStoreNameSemanticsDto
{
    public string? ProfileId { get; set; }
    public int? Encoding { get; set; }
    public bool? CaseSensitive { get; set; }
    public bool? NormalizationInsensitive { get; set; }

    internal PhysicalStoreNameSemantics ToSemantics()
    {
        var encodingValue = Encoding ?? throw new JsonException("A native name profile is missing its encoding.");
        var encoding = (PhysicalStoreNameEncoding)Enum.ToObject(typeof(PhysicalStoreNameEncoding), encodingValue);
        if (!Enum.IsDefined(encoding))
            throw new JsonException("A native name profile has an unsupported encoding.");
        return new PhysicalStoreNameSemantics(
            ProfileId ?? throw new JsonException("A native name profile is missing its profile ID."),
            encoding,
            CaseSensitive ?? throw new JsonException("A native name profile is missing its case-sensitivity flag."),
            NormalizationInsensitive ?? throw new JsonException("A native name profile is missing its normalization flag."));
    }

    internal static PhysicalStoreNameSemanticsDto FromSemantics(PhysicalStoreNameSemantics semantics)
        => new()
        {
            ProfileId = semantics.ProfileId,
            Encoding = (int)semantics.Encoding,
            CaseSensitive = semantics.CaseSensitive,
            NormalizationInsensitive = semantics.NormalizationInsensitive
        };
}

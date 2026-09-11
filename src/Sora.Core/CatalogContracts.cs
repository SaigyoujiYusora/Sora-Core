using System.Text.Json.Serialization;

namespace Sora.Core;

// Temporary dependency placement for the reconstructed SRED v3 commit. These
// declarations move back to their source files when the catalog stage lands.
public sealed record GameCatalogSource(string ManifestHash, string ManifestRevision, string Coverage, int AddressCount, int BundleCount,
    string MetadataStatus = "not-loaded", ResourceFileRecord[]? MetadataFiles = null, CatalogMetadataSource[]? MetadataSources = null,
    string AliasPolicy = "lowest-available-bundle-index");
public sealed record AssetLocator(string Parser, string Path, string? Hash = null, int? Bundle = null, string MetadataStatus = "unparsed",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[]? BundleCandidates = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ResourceFileRecord? Source = null);
public sealed record ImportCapability(string State, bool CanImport, string Reason, bool CanResolve = false, bool CanAttemptImport = false,
    string DependencyStatus = "unknown", string StructureStatus = "unparsed", int? SelectedBundle = null);
public sealed record CatalogAssetMetadata(string InternalName, string? DisplayNameZh, string? DisplayNameEn, string LocalizationStatus,
    string SourceTable, string RowId, string NameTextId, string? DefaultWeaponId = null, int? WeaponType = null, string? LocalizationDetail = null);
public sealed record CatalogMetadataSource(string Path, bool Available, ResourceFileRecord? Source);

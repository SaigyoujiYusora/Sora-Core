namespace Sora.Core;

// Temporary dependency declaration for the reconstructed validation stage.
// The concrete reader replaces this file in the catalog stage.
public static class NativeCatalogMetadata
{
    public static readonly string[] SourcePaths = ["TableCfg/CharacterTable.bytes", "TableCfg/WeaponBasicTable.bytes", "TableCfg/I18nTextTable_CN.bytes", "TableCfg/ItemTable.bytes"];
}

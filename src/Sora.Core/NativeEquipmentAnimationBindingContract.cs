namespace Sora.Core;

// Temporary dependency declaration for the equipment sampling stage.
// The concrete binding service owns this record once the lifecycle stage lands.
public sealed record NativeEquipmentAnimationBinding(uint PathHash, int Attribute, int Bone, string SourcePath);

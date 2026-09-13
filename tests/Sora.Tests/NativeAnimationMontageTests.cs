using System.Buffers.Binary;
using System.Text;
using Sora.Core;

static class NativeAnimationMontageTests
{
    public static void Run(Action<string, Action> test, Action<Action> reject)
    {
        test("AnimationConfigJson preserves the verified non-empty bone weight mask record", () =>
        {
            const ulong hash = 0x0bb397e66555938b;
            var map = NativeAnimationMontageConfig.Read(ConfigFixture(true), "fixture");
            if (!map.MontagesComplete || map.Montages.Length != 0 || map.BoneWeightMasks is null
                || map.BoneWeightMasks.Length != 1 || map.BoneWeightMasks[0] != new NativeAnimationBoneWeightMask("MSBowOverrideLayer", hash)
                || map.ControllerHash != 0x018e848d699167dc)
                throw new Exception("Non-empty bone weight mask was not consumed or retained");
        });
        test("AnimationConfigJson keeps the empty bone weight mask path", () =>
        {
            var map = NativeAnimationMontageConfig.Read(ConfigFixture(false), "fixture");
            if (!map.MontagesComplete || map.Montages.Length != 0 || map.BoneWeightMasks is null || map.BoneWeightMasks.Length != 0)
                throw new Exception("Empty bone weight mask path changed");
        });
        test("AnimationConfigJson consumes a bone weight mask whose layer name length differs", () =>
        {
            var map = NativeAnimationMontageConfig.Read(ConfigFixture(true, 2, "ShortMask"), "fixture");
            if (!map.MontagesComplete || map.BoneWeightMasks is null || map.BoneWeightMasks.Length != 1
                || map.BoneWeightMasks[0] != new NativeAnimationBoneWeightMask("ShortMask", 0x0bb397e66555938b)
                || map.ControllerHash != 0x018e848d699167dc)
                throw new Exception("Variable-length bone weight mask name was not consumed");
        });        test("AnimationConfigJson rejects an unverified bone weight mask union tag", () =>
            reject(() => NativeAnimationMontageConfig.Read(ConfigFixture(true, 3), "fixture")));
    }

    private static void I32(List<byte> bytes, int value) => bytes.AddRange(BitConverter.GetBytes(value));
    private static void U64(List<byte> bytes, ulong value) => bytes.AddRange(BitConverter.GetBytes(value));
    private static void F32(List<byte> bytes, float value) => bytes.AddRange(BitConverter.GetBytes(value));
    private static void Str(List<byte> bytes, string value)
    {
        I32(bytes, Encoding.UTF8.GetByteCount(value));
        bytes.AddRange(Encoding.UTF8.GetBytes(value));
    }

    private static byte[] ConfigFixture(bool mask, byte maskTag = 2, string maskName = "MSBowOverrideLayer")
    {
        var bytes = new List<byte> { 15, 255 };
        bytes.AddRange(new byte[16]);
        I32(bytes, mask ? 1 : 0);
        if (mask)
        {
            bytes.Add(maskTag);
            Str(bytes, maskName);
            U64(bytes, 0x0bb397e66555938b);
        }
        U64(bytes, 0x018e848d699167dc);
        bytes.Add(0);
        bytes.Add(30);
        bytes.Add(255); // animated shader properties
        bytes.Add(255); // animation effect properties
        bytes.Add(255); // renderer visibility properties
        bytes.AddRange(new byte[3]); // three booleans
        for (int index = 0; index < 4; index++) F32(bytes, 0);
        I32(bytes, 0);
        for (int index = 0; index < 4; index++) F32(bytes, 0);
        bytes.Add(0); // has special dash
        bytes.Add(255); // hurt animations null
        F32(bytes, 0);
        bytes.Add(0); // hold bomb with both hands
        F32(bytes, 0);
        F32(bytes, 0);
        F32(bytes, 0);
        bytes.Add(255); // move additive animations null
        bytes.Add(0); // override dash duration
        bytes.Add(255); // override perform dictionary null
        I32(bytes, 0);
        bytes.Add(255); // special dash null
        bytes.Add(255); // special idle null
        I32(bytes, 0); // state perform entries
        I32(bytes, 0); // walk special loop count
        bytes.Add(255); // montage dictionary null
        return bytes.ToArray();
    }
}

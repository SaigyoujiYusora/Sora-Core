using Sora.Core;

static class NativeAnimationServiceTests
{
    public static void Run(Action<string, Action> test, Action<Action> reject)
    {
        var manifest = new NativeManifest("fixture", "hash", "", 0, [],
        [
            new(1, "assets/animations/a_actor_azrila_attack_02.fbx", 0, 1),
            new(2, "assets/a_actor_azrila_attack_01.anim", 0, 1),
            new(3, "assets/a_actor_azrilax_attack_01.anim", 0, 1),
            new(4, "assets/a_actor_azrila_attack_01.controller", 0, 1),
            new(5, "assets/a_actor_azrila_attack_01.anim", 0, 1),
            new(6, "assets/models/a_actor_azrila_attack_01.fbx", 0, 1)
        ]);
        test("native animation search scopes exact character tokens and stable resource identities", () =>
        {
            var rows = NativeAnimationService.Search(manifest, "ATTACK 01", "assets/chr_0009_azrila_uimodel.prefab");
            Check(rows.Length == 1 && rows[0].Id == "assets/a_actor_azrila_attack_01.anim" && rows[0].Path == rows[0].Id);
            Check(NativeAnimationService.Search(manifest, "attack", "assets/chr_0009_azrila_uimodel.prefab", 1)[0].Id == rows[0].Id);
            Check(NativeAnimationService.Search(manifest, "", null).Length == 3);
        });
        test("native animation search rejects invalid windows and queries", () =>
        {
            reject(() => NativeAnimationService.Search(manifest, "bad\nquery"));
            reject(() => NativeAnimationService.Search(manifest, new string('x', 513)));
            reject(() => NativeAnimationService.Search(manifest, "", limit: 0));
            reject(() => NativeAnimationService.Search(manifest, "", limit: 1001));
            Check(NativeAnimationService.CharacterToken("assets/chr_0010_actor_variant_uimodel.prefab") == "actor_variant");
            Check(NativeAnimationService.CharacterToken("assets/chr_bad_actor_uimodel.prefab") is null);
        });
    }

    private static void Check(bool condition) { if (!condition) throw new Exception("Native animation service assertion failed"); }
}

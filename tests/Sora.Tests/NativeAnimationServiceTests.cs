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
        test("animation pages retain total beyond 200 and mark name rules as inferred",()=>{
            var many=new NativeManifest("fixture","hash","",0,[],Enumerable.Range(0,1105).Select(i=>new AddressResource(i,"assets/animations/a_actor_azrila_attack_"+i.ToString("D4")+".fbx",0,1)).ToArray());
            var rows=NativeAnimationCatalog.Discover(many,"assets/chr_0009_azrila_uimodel.prefab");
            var first=NativeAnimationCatalog.Page(rows,"",0,200,"attack");var last=NativeAnimationCatalog.Page(rows,"",1000,200,"attack");
            Check(first.Total==1105&&first.Rows.Length==200&&last.Total==1105&&last.Rows.Length==105);
            Check(first.Rows.All(row=>row.Classification.Confidence=="inferred")&&NativeAnimationCatalog.Classify("unknown.fbx").Category=="unclassified");
            Check(NativeAnimationCatalog.Classify("assets/a_fx_runicstone_lock.anim").Category=="unclassified"&&NativeAnimationCatalog.Classify("assets/a_actor_boy_run_loop.fbx").Category=="move");
            reject(()=>NativeAnimationCatalog.Page(rows,"",-1,20));reject(()=>NativeAnimationCatalog.Page(rows,"",0,20,"invented"));
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

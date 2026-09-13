using Sora.Core;

internal static class NativeWeaponAdaptationTests
{
    public static void Run(Action<string,Action> test,Action<Action> reject)
    {
        static Dictionary<string,NativeWeaponTypeDeclaration> ByPath(params NativeWeaponTypeDeclaration[] rows)=>rows.ToDictionary(row=>row.ResourcePath,StringComparer.Ordinal);
        static Dictionary<string,NativeWeaponTypeDeclaration> ById(params NativeWeaponTypeDeclaration[] rows)=>rows.ToDictionary(row=>row.WeaponId,StringComparer.Ordinal);
        const string Root="assets/beyond/dynamicassets/gameplay/prefabs/weapons/";
        var sword=new NativeWeaponTypeDeclaration("wpn_sword_0001",Root+"wpn_sword_0001.prefab",1);
        var funnelA=new NativeWeaponTypeDeclaration("wpn_funnel_0008",Root+"wpn_funnel_0010.prefab",2);
        var funnelB=new NativeWeaponTypeDeclaration("wpn_funnel_0010",Root+"wpn_funnel_0008.prefab",2);

        test("declared weapon resource is confirmed native type",()=>{
            var result=NativeWeaponAssembly.Recognize(sword.ResourcePath,ByPath(sword),ById(sword));
            if(result.Status!="confirmed-native-type"||result.Confidence!="confirmed"||result.WeaponType!=1||result.BaseWeaponId is not null||result.BaseIdentityStatus!="not-applicable")throw new Exception();
        });
        test("refined weapon infers type when both associations agree and flags ambiguous base ownership",()=>{
            string path=Root+"wpn_funnel_0008_refined.prefab";
            var result=NativeWeaponAssembly.Recognize(path,ByPath(funnelA,funnelB),ById(funnelA,funnelB));
            if(result.Status!="inferred-native-base-type"||result.Confidence!="inferred"||result.WeaponType!=2||result.BaseWeaponId!="wpn_funnel_0008")throw new Exception();
            if(result.BaseIdentityStatus!="ambiguous")throw new Exception("Swapped sibling must be disclosed as ambiguous, not confirmed");
            if(!result.Message.Contains("ambiguous"))throw new Exception();
        });
        test("refined weapon with agreeing base path reports consistent ownership",()=>{
            var row=new NativeWeaponTypeDeclaration("wpn_sword_0007",Root+"wpn_sword_0007.prefab",1);
            string path=Root+"wpn_sword_0007_refined.prefab";
            var result=NativeWeaponAssembly.Recognize(path,ByPath(row),ById(row));
            if(result.Status!="inferred-native-base-type"||result.WeaponType!=1||result.BaseIdentityStatus!="consistent")throw new Exception();
        });
        test("conflicting native associations never pick a side",()=>{
            var byIdOnly=new NativeWeaponTypeDeclaration("wpn_lance_0004",Root+"wpn_sword_0003.prefab",5);
            var pathRow=new NativeWeaponTypeDeclaration("wpn_lance_0005",Root+"wpn_lance_0004.prefab",1);
            string path=Root+"wpn_lance_0004_refined.prefab";
            var declaredByPath=ByPath(pathRow,byIdOnly);var declaredById=ById(byIdOnly,pathRow);
            var result=NativeWeaponAssembly.Recognize(path,declaredByPath,declaredById);
            if(result.Status!="unknown-native-weapon-type"||result.Confidence!="unknown"||result.WeaponType is not null)throw new Exception();
            if(result.BaseIdentityStatus!="conflicting"||!result.Message.Contains("conflict"))throw new Exception();
        });
        test("undeclared non-refined weapon stays unknown and never guessed",()=>{
            string path=Root+"wpn_misc_9999.prefab";
            var result=NativeWeaponAssembly.Recognize(path,ByPath(sword),ById(sword));
            if(result.Status!="unknown-native-weapon-type"||result.Confidence!="unknown"||result.WeaponType is not null||result.BaseWeaponId is not null)throw new Exception();
        });
        test("refined name without any declared base stays unknown",()=>{
            string path=Root+"wpn_lance_9999_refined.prefab";
            var result=NativeWeaponAssembly.Recognize(path,ByPath(sword),ById(sword));
            if(result.Status!="unknown-native-weapon-type"||result.WeaponType is not null)throw new Exception();
        });
        test("refined variant with only the name association is incomplete evidence",()=>{
            var row=new NativeWeaponTypeDeclaration("wpn_sword_0001",Root+"wpn_sword_0002.prefab",1);
            string path=Root+"wpn_sword_0001_refined.prefab";
            var result=NativeWeaponAssembly.Recognize(path,ByPath(row),ById(row));
            if(result.Status!="unknown-native-weapon-type"||result.Confidence!="unknown"||result.WeaponType is not null)throw new Exception();
            if(result.BaseIdentityStatus!="incomplete"||!result.Message.Contains("name-derived base id 'wpn_sword_0001' resolves")||!result.Message.Contains("base resource path"))throw new Exception();
        });
        test("refined variant with only the base-path association is incomplete evidence",()=>{
            var row=new NativeWeaponTypeDeclaration("wpn_sword_0001",Root+"wpn_sword_0002.prefab",1);
            string path=Root+"wpn_sword_0002_refined.prefab";
            var result=NativeWeaponAssembly.Recognize(path,ByPath(row),ById(row));
            if(result.Status!="unknown-native-weapon-type"||result.Confidence!="unknown"||result.WeaponType is not null)throw new Exception();
            if(result.BaseIdentityStatus!="incomplete"||!result.Message.Contains("base resource path '"+Root+"wpn_sword_0002.prefab' resolves")||!result.Message.Contains("name-derived base id 'wpn_sword_0002' does not"))throw new Exception();
        });
        test("inferred recognition source is disclosed so a caller can preserve provenance",()=>{
            var row=new NativeWeaponTypeDeclaration("wpn_sword_0007",Root+"wpn_sword_0007.prefab",1);
            string path=Root+"wpn_sword_0007_refined.prefab";
            var result=NativeWeaponAssembly.Recognize(path,ByPath(row),ById(row));
            if(result.Status!="inferred-native-base-type"||result.Source.Contains("WeaponBasicTable") is false)throw new Exception();
            if(result.Source.Contains("_refined.prefab") is false)throw new Exception("inferred source must name the refined-name association");
        });
    }
}

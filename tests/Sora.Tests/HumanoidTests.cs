using System.Numerics;
using System.Text.Json;
using Sora.Core;

internal static class HumanoidTests
{
    public static void Run(Action<string, Action> test, Action<Action> reject)
    {
        var rig = Rig(); var neutral = Frame();
        test("native61 independent spine and asymmetric toe axes", () => {
            var body = new float[61]; body[0] = .4f; body[29] = -.3f;
            var result = NativeHumanoidPose.Evaluate(rig, neutral with { BodyMuscles = body });
            Near(result.Body.Single(x => x.HumanSlot == 7).Rotation, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .4f));
            Near(result.Body.Single(x => x.HumanSlot == 20).Rotation, Quaternion.CreateFromAxisAngle(Vector3.UnitY, -.3f));
            Near(result.Body.Single(x => x.HumanSlot == 21).Rotation, Quaternion.Identity);
        });
        test("native zero forearm twist preserves chain rotation on the named side", () => {
            var body = new float[61]; body[49] = .7f;
            var result = NativeHumanoidPose.Evaluate(rig, neutral with { BodyMuscles = body });
            Near(result.Body.Single(x => x.HumanSlot == 16).Rotation, Quaternion.Identity);
            Near(result.Body.Single(x => x.HumanSlot == 18).Rotation, Quaternion.CreateFromAxisAngle(Vector3.UnitX, .7f));
            Near(result.Body.Single(x => x.HumanSlot == 17).Rotation, Quaternion.Identity);
            Near(result.Body.Single(x => x.HumanSlot == 19).Rotation, Quaternion.Identity);
        });
        test("native zero proximal twist preserves folded multi-axis limb chains", () => {
            var body = new float[61];
            foreach (int channel in new[] { 21, 22, 23, 24, 25, 26, 27, 28, 43, 44, 45, 46, 47, 48, 49, 50, 51 })
                body[channel] = channel % 2 == 0 ? -.37f : .61f;
            var input = neutral with { BodyMuscles = body };
            var player = NativeHumanoidPose.Evaluate(rig, input).Body.ToDictionary(x => x.HumanSlot, x => x.Rotation);
            var npcRig = Copy(rig, policy: Vector4.Zero);
            if (npcRig.TwistPolicy != Vector4.Zero) throw new Exception("Native twist parameters were lost");
            var npc = NativeHumanoidPose.Evaluate(npcRig, input).Body.ToDictionary(x => x.HumanSlot, x => x.Rotation);
            foreach (var (upper, lower, end) in new[] { (1, 3, 5), (2, 4, 6), (14, 16, 18), (15, 17, 19) })
            {
                Near(npc[upper] * npc[lower] * npc[end], player[upper] * player[lower] * player[end]);
                if (Math.Abs(npc[upper].X) > 1e-6f) throw new Exception("Proximal twist remained on the zero-policy parent");
            }
            if (Math.Abs(Quaternion.Dot(npc[14], player[14])) > .999f) throw new Exception("Nonzero arm twist fixture did not exercise redistribution");
        });
        test("native root removes full nonidentity motion frame", () => {
            var body = new float[61]; body[0] = .25f; body[43] = -.3f;
            var baseline = neutral with { BodyMuscles = body, RootTranslation = new(.2f, 1.2f, -.4f), RootRotation = Quaternion.CreateFromYawPitchRoll(.3f, .1f, -.2f) };
            var expected = NativeHumanoidPose.Evaluate(rig, baseline); var motion = Quaternion.CreateFromYawPitchRoll(.7f, -.2f, .3f); var offset = new Vector3(.4f, -.2f, .1f);
            var moved = NativeHumanoidPose.Evaluate(rig, baseline with { MotionRotation = motion, MotionTranslation = offset, RootRotation = motion * baseline.RootRotation, RootTranslation = offset + Vector3.Transform(baseline.RootTranslation, motion) });
            if (Vector3.Distance(expected.HipsTranslation, moved.HipsTranslation) > 1e-5f) throw new Exception("Motion frame changed body result"); Near(expected.HipsRotation, moved.HipsRotation);
        });
        test("native pelvis mass center uses the physical triangle", () => {
            var result = NativeHumanoidPose.Evaluate(rig, neutral);
            var expected = neutral.RootTranslation - new Vector3(0, (.9f + .9f + 1.1f) / 3 - 1, 0);
            if (Vector3.Distance(result.HipsTranslation, expected) > 1e-6f) throw new Exception("Incorrect pelvis center");
        });
        test("native61 rejects absent targets fingers bad counts and nonfinite motion", () => {
            reject(() => NativeHumanoidPose.Evaluate(rig, neutral with { BodyMuscles = new float[55] }));
            var eyes = new float[61]; eyes[15] = .1f; reject(() => NativeHumanoidPose.Evaluate(rig, neutral with { BodyMuscles = eyes }));
            var fingers = new float[40]; fingers[0] = .2f; reject(() => NativeHumanoidPose.Evaluate(rig, neutral with { FingerMuscles = fingers }));
            reject(() => NativeHumanoidPose.Evaluate(rig, neutral with { RootRotation = default }));
            reject(() => NativeHumanoidPose.Evaluate(rig, neutral with { MotionTranslation = new(float.NaN, 0, 0) }));
        });
        test("native rig refuses ambiguous identities missing core nodes and unsupported policies", () => {
            var nodes = rig.Nodes.ToArray(); nodes[2] = nodes[2] with { PathHash = nodes[1].PathHash }; reject(() => Copy(rig, nodes: nodes));
            nodes = rig.Nodes.ToArray(); nodes[2] = nodes[2] with { Parent = 3 }; reject(() => Copy(rig, nodes: nodes));
            var slots = rig.HumanNodes.ToArray(); slots[5] = -1; reject(() => Copy(rig, slots: slots));
            reject(() => Copy(rig, policy: new(1, .5f, 1, 0)));
            foreach (var policy in new[] { new Vector4(0, 0, 1, 0), new Vector4(.5f, 0, .5f, 0), new Vector4(float.NaN, 0, 0, 0) })
                reject(() => Copy(rig, policy: policy));
        });
        test("native rig snapshots identity and mass arrays", () => {
            var nodes = rig.Nodes.ToArray(); var masses = rig.Masses.ToArray(); var copied = Copy(rig, nodes: nodes, masses: masses); nodes[1] = nodes[1] with { Path = "changed" }; masses[0] = 0;
            if (copied.Nodes[1].Path == "changed" || copied.Masses[0] != 1) throw new Exception("Mutable input leaked into rig");
        });
        test("native Animator custom type 8 and unsigned attributes bind by identity", () => {
            var (clip,samples)=BindingFixture();var plan=new NativeAnimationBinding(clip,samples,RootFixture(samples));
            if(plan.HumanFrame(0).BodyMuscles[0] != .4f || plan.CustomScalarAttributes.Single()!=3452271111u)throw new Exception("Scalar identity mapping changed");
            reject(()=>new NativeAnimationBinding(JsonDocument.Parse(clip.GetRawText().Replace("\"customType\":8","\"customType\":9")).RootElement,samples));
            reject(()=>new NativeAnimationBinding(JsonDocument.Parse(clip.GetRawText().Replace("\"attribute\":142","\"attribute\":141")).RootElement,samples));
        });
        test("native combined clip preserves exact identity and imported rest basis", () => {
            var (clip,samples)=BindingFixture();var plan=new NativeAnimationBinding(clip,samples,RootFixture(samples));var allPaths=rig.SourcePaths.ToDictionary(x=>x.Key,x=>x.Value);allPaths.Add(999,"extra");
            var allParents=rig.SourceParents.ToDictionary(x=>x.Key,x=>x.Value);allParents.Add(999,0);
            var source=new NativeHumanoidRig(rig.Nodes.ToArray(),rig.Axes.ToArray(),rig.HumanNodes.ToArray(),rig.Masses.ToArray(),1,new(1,0,1,0),allPaths,allParents);
            var native=rig.Nodes.ToArray().Append(new(0,999,"extra",new(.1f,.2f,.3f),Quaternion.Identity,-1)).ToArray();var world=new Matrix4x4[native.Length];var bones=new BoneRecord[native.Length];var f=Matrix4x4.CreateScale(-1,1,1);var c=f*Matrix4x4.CreateRotationX(MathF.PI/2);
            for(int i=0;i<native.Length;i++){var node=native[i];world[i]=Matrix4x4.CreateFromQuaternion(node.Rotation)*Matrix4x4.CreateTranslation(node.Translation)*(node.Parent<0?Matrix4x4.Identity:world[node.Parent]);var rest=f*world[i]*c;var head=rest.Translation;var tail=head+Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY,rest))*.05f;bones[i]=new("bone"+i,node.Parent,[head.X,head.Y,head.Z],[tail.X,tail.Y,tail.Z],RestMatrix:Values(rest),SourcePath:node.Path,SourceHash:node.PathHash);}
            var scene=new SceneDocument("synthetic",bones,[],[],[]);var converted=NativeAnimationClip.Convert("test",plan,source,scene);var result=converted.Clip;
            if(converted.CustomScalars.Length!=1||converted.CustomScalars[0].Values.Length!=2||converted.Diagnostics.Length!=1)throw new Exception("Custom scalar metadata was dropped");
            if(result.Tracks.Length!=69||result.Tracks.Any(x=>x.Keys.Length!=2))throw new Exception("Combined channel coverage changed");
            var wrong=(BoneRecord[])bones.Clone();wrong[^1]=wrong[^1] with{SourcePath="different"};reject(()=>NativeAnimationClip.Convert("test",plan,source,scene with{Bones=wrong}));
            wrong=(BoneRecord[])bones.Clone();wrong[^1]=wrong[^1] with{Parent=1};reject(()=>NativeAnimationClip.Convert("test",plan,source,scene with{Bones=wrong}));
            var changedParents=new Dictionary<uint,uint?>(allParents){[999]=1};
            var wrongNative=new NativeHumanoidRig(rig.Nodes.ToArray(),rig.Axes.ToArray(),rig.HumanNodes.ToArray(),rig.Masses.ToArray(),1,new(1,0,1,0),allPaths,changedParents);
            reject(()=>NativeAnimationClip.Convert("test",plan,wrongNative,scene));
            var (genericClip,genericSamples,genericRoot)=GenericFixture();var generic=new NativeAnimationBinding(genericClip,genericSamples,genericRoot);
            var genericResult=NativeAnimationClip.Convert("generic-ui",generic,source,scene).Clip;
            if(generic.HasHumanoid||genericResult.Tracks.Length!=3||genericResult.Tracks.Any(x=>x.Bone!=0))throw new Exception("Generic UI clip injected humanoid tracks");
            reject(()=>generic.HumanFrame(0));
        });
        test("native binding rejects root disagreement sparse masks defaults and timing", () => {
            var (clip,samples)=BindingFixture();var root=RootFixture(samples);root.Values[10]=1;reject(()=>new NativeAnimationBinding(clip,samples,root));
            string json=clip.GetRawText();
            foreach(string changed in new[]{json.Replace("gIAAAAAAAAA=","gAAAAAAAAAA="),json.Replace("\"m_DefaultIndexs\":{\"Array\":[]}","\"m_DefaultIndexs\":{\"Array\":[0]}"),json.Replace("\"m_StartTime\":0","\"m_StartTime\":1"),json.Replace("\"m_ConstantValues\":{\"Array\":[]}","\"m_ConstantValues\":{\"Array\":[1]}")})reject(()=>new NativeAnimationBinding(JsonDocument.Parse(changed).RootElement,samples));
        });
        test("native sparse translation uses exact rotation-mask identity and imported rest", () => {
            var (clip,samples)=BindingFixture();var json=System.Text.Json.Nodes.JsonNode.Parse(clip.GetRawText())!;
            json["m_ClipBindingConstant"]!["genericBindings"]!["Array"]!.AsArray().RemoveAt(0);
            json["m_AclCompressedBuffer"]!["TransformSubTrackMasks"]!["Array"]="AIAAAAAAAAA=";
            var plan=new NativeAnimationBinding(JsonSerializer.SerializeToElement(json),samples,RootFixture(samples));
            var local=plan.TransformLocal(0,0,Matrix4x4.CreateScale(2,3,4)*Matrix4x4.CreateTranslation(1,2,3));
            if(plan.TransformPaths[0]!=999||plan.PositionTracks[0]||!plan.RotationTracks[0]||Vector3.Distance(local.Translation,new(1,2,3))>1e-6)throw new Exception("Unbound translation collapsed to ACL zero default");
            reject(()=>plan.TransformLocal(0,0));
        });
        test("native channel masks reject nonzero alignment padding", () => {
            var (clip,samples)=BindingFixture();var json=System.Text.Json.Nodes.JsonNode.Parse(clip.GetRawText())!;
            json["m_AclCompressedBuffer"]!["TransformSubTrackMasks"]!["Array"]=Convert.ToBase64String([128,128,0,0,0,0,0,1]);
            reject(()=>new NativeAnimationBinding(JsonSerializer.SerializeToElement(json),samples));
        });
        test("independent root quantization is bounded and never changes primary values", () => {
            var (clip,samples)=BindingFixture();var root=RootFixture(samples);root.Values[10]=.00008f;
            var binding=new NativeAnimationBinding(clip,samples,root);
            if(binding.ScalarValue(0,0)!=0||binding.RootComparisonMaximumError!=.00008f)throw new Exception("Root tolerance altered authoritative primary data");
            root.Values[10]=.0002f;reject(()=>new NativeAnimationBinding(clip,samples,root));
            foreach(float invalid in new[]{float.NaN,float.PositiveInfinity}){root.Values[10]=invalid;reject(()=>new NativeAnimationBinding(clip,samples,root));}
            root=RootFixture(samples);root.Values[10+19]=.00008f;binding=new NativeAnimationBinding(clip,samples,root);
            if(binding.ScalarValue(0,19)!=0||binding.RootComparisonMaximumError!=.00008f)throw new Exception("Quaternion component comparison changed primary data");
        });
        test("generic root21 checks its transform tail separately from scalar prefix", () => {
            var (clip,samples,root)=GenericFixture();root.Values[10+14]=.000008f;root.Values[10+17]=.0000003f;
            var binding=new NativeAnimationBinding(clip,samples,root);if(binding.HasHumanoid||binding.ScalarValue(0,7)!=0)throw new Exception();
            root.Values[10+14]=.00002f;reject(()=>new NativeAnimationBinding(clip,samples,root));
            root.Values[10+14]=0;root.Values[10+17]=float.NaN;reject(()=>new NativeAnimationBinding(clip,samples,root));
            root.Values[10+17]=0;root.Values[10+20]=-1;new NativeAnimationBinding(clip,samples,root);
        });
        test("SRED v2 persists exact native clip source scalar identities and samples", () => {
            var scene=MetadataScene();var database=new DatabaseDocument("v",[new("a","A","","character",[],scene)]);
            using var stream=new MemoryStream();DatabaseFile.Write(stream,database);stream.Position=0;var result=DatabaseFile.Read(stream).Assets[0].Scene!.Clips[0];
            if(result.Native!.Source!.PathId!=long.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture)||result.Native.CustomScalars[0].Path!=uint.MaxValue||result.Native.CustomScalars[0].Attribute!=uint.MaxValue||!result.Native.CustomScalars[0].Values.SequenceEqual(new[]{.25f,-.125f})||result.Native.CustomScalars[0].TypeId!=95||result.Native.CustomScalars[0].CustomType!=0||result.Native.CustomScalars[0].SampleRate!=60||result.Native.Diagnostics.Single()!="unapplied")throw new Exception("Persisted native clip metadata changed");
        });
        test("native clip metadata rejects duplicate identities invalid rate counts values and source", () => {
            var scene=MetadataScene();var clip=scene.Clips[0];var metadata=clip.Native!;var scalar=metadata.CustomScalars[0];
            foreach(var invalid in new[]{metadata with{CustomScalars=[scalar,scalar]},metadata with{CustomScalars=[scalar with{SampleRate=30}]},metadata with{CustomScalars=[scalar with{Values=[1]}]},metadata with{CustomScalars=[scalar with{Values=[float.NaN,1]}]},metadata with{CustomScalars=[scalar with{TypeId=4}]},metadata with{CustomScalars=[scalar with{CustomType=8}]},metadata with{CustomScalars=[null!]},metadata with{Source=metadata.Source! with{PathId="9223372036854775808"}},metadata with{Diagnostics=null!}})
                reject(()=>Validation.Scene(scene with{Clips=[clip with{Native=invalid}]}));
        });
        test("legacy v1 clips omit native metadata and v1 cannot smuggle new fields", () => {
            const string legacy="""{"gameVersion":"legacy","assets":[{"id":"a","label":"A","detail":"","kind":"character","dependencies":[],"scene":{"name":"s","bones":[],"meshes":[],"materials":[],"clips":[{"name":"c","duration":0,"fps":60,"tracks":[]}]}}]}""";
            var parsed=DatabaseFile.ParsePayload(System.Text.Encoding.UTF8.GetBytes(legacy),1);if(parsed.Assets[0].Scene!.Clips[0].Native is not null)throw new Exception("Invented legacy native metadata");
            reject(()=>DatabaseFile.ParsePayload(System.Text.Encoding.UTF8.GetBytes(legacy.Replace("\"tracks\":[]","\"tracks\":[],\"native\":null")),1));
        });
    }

    private static NativeHumanoidFrame Frame() => new(Vector3.Zero, Quaternion.Identity, new(.2f, 1, .3f), Quaternion.Identity, new float[61], new float[40]);
    private static SceneDocument MetadataScene()
    {
        var source=new NativeAnimationSource("assets/test.anim","CAB-test",long.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture),"manifest");
        var metadata=new NativeAnimationMetadata(source,[new(uint.MaxValue,95,0,uint.MaxValue,60,[.25f,-.125f])],["unapplied"]);
        return new("s",[],[],[],[new("c",1/60d,60,[],metadata)]);
    }
    private static (JsonElement Clip,AclSamples Samples) BindingFixture()
    {
        var bindings=new List<object>{new{path=999u,attribute=1u,typeID=4,customType=0,isPPtrCurve=0},new{path=999u,attribute=2u,typeID=4,customType=0,isPPtrCurve=0}};
        uint[] attributes=[..Enumerable.Range(0,143).Reverse().Select(x=>(uint)x),3452271111u];foreach(uint attribute in attributes)bindings.Add(new{path=0u,attribute,typeID=95,customType=attribute<143?8:0,isPPtrCurve=0});
        var clip=JsonSerializer.SerializeToElement(new{m_aclType=16,m_SampleRate=60,m_MuscleClip=new{m_StartTime=0,m_StopTime=1/60f,m_DeltaPose=new{m_DoFArray=new{Array=new float[61]}}},m_ClipBindingConstant=new{genericBindings=new{Array=bindings}},m_AclCompressedBuffer=new{Header=new{Version=10},OutputTrackCount=1,FloatCurveCount=144,RootPosIndex=65535,RootRotIndex=65535,RootScaleIndex=65535,RootTrackCount=28,m_DefaultIndexs=new{Array=System.Array.Empty<int>()},m_ConstantIndexs=new{Array=System.Array.Empty<int>()},m_ConstantValues=new{Array=System.Array.Empty<float>()},TransformSubTrackConstantMasks=new{Array=""},TransformSubTrackMasks=new{Array="gIAAAAAAAAA="}}});
        var values=new float[2*154];for(int sample=0;sample<2;sample++){int start=sample*154;values[start+3]=values[start+7]=values[start+8]=values[start+9]=1;for(int i=0;i<attributes.Length;i++)values[start+10+i]=attributes[i] is 6 or 13 or 20 or 27 or 34 or 41?1:attributes[i]==42?.4f:0;}
        return(clip,new(1,144,2,60,values));
    }
    private static double[] Values(Matrix4x4 m)=>[m.M11,m.M21,m.M31,m.M41,m.M12,m.M22,m.M32,m.M42,m.M13,m.M23,m.M33,m.M43,m.M14,m.M24,m.M34,m.M44];
    private static AclSamples RootFixture(AclSamples samples)
    {
        var values=new float[76];for(int sample=0;sample<2;sample++){System.Array.Copy(samples.Values,sample*154,values,sample*38,10);for(int attribute=0;attribute<28;attribute++)values[sample*38+10+attribute]=samples.Values[sample*154+10+142-attribute];}return new(1,28,2,60,values);
    }
    private static (JsonElement Clip,AclSamples Samples,AclSamples Root) GenericFixture()
    {
        var (clip,_)=BindingFixture();var json=System.Text.Json.Nodes.JsonNode.Parse(clip.GetRawText())!;var rows=json["m_ClipBindingConstant"]!["genericBindings"]!["Array"]!.AsArray();rows.Clear();
        foreach(uint attribute in new uint[]{1,2})rows.Add(JsonSerializer.SerializeToNode(new{path=0u,attribute,typeID=4,customType=0,isPPtrCurve=0}));
        foreach(uint attribute in Enumerable.Range(0,14).Select(x=>(uint)x).Append(3452271111u))rows.Add(JsonSerializer.SerializeToNode(new{path=0u,attribute,typeID=95,customType=attribute<143?8:0,isPPtrCurve=0}));
        var buffer=json["m_AclCompressedBuffer"]!;buffer["FloatCurveCount"]=15;buffer["RootTrackCount"]=21;buffer["RootPosIndex"]=0;buffer["RootRotIndex"]=0;
        var values=new float[50];var roots=new float[62];for(int sample=0;sample<2;sample++){foreach(int column in new[]{3,7,8,9}){values[sample*25+column]=1;roots[sample*31+column]=1;}foreach(int column in new[]{6,13}){values[sample*25+10+column]=1;roots[sample*31+10+column]=1;}roots[sample*31+30]=1;values[sample*25+24]=.5f;}
        return(JsonSerializer.SerializeToElement(json),new(1,15,2,60,values),new(1,21,2,60,roots));
    }
    private static void Near(Quaternion a, Quaternion b) { if (1 - Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))) > 1e-6) throw new Exception("Quaternion mismatch"); }
    private static NativeHumanoidRig Copy(NativeHumanoidRig rig, NativeHumanNode[]? nodes = null, int[]? slots = null, float[]? masses = null, Vector4? policy = null)
        => new(nodes ?? rig.Nodes.ToArray(), rig.Axes.ToArray(), slots ?? rig.HumanNodes.ToArray(), masses ?? rig.Masses.ToArray(), 1, policy ?? new(1, 0, 1, 0));
    private static NativeHumanoidRig Rig()
    {
        int[] parents = [-1, 0, 0, 1, 2, 3, 4, 0, 7, 8, 9, 10, 9, 9, 12, 13, 14, 15, 16, 17, 5, 6];
        Vector3[] world = [new(0,1,0),new(-.15f,.9f,0),new(.15f,.9f,0),new(-.15f,.5f,0),new(.15f,.5f,0),new(-.15f,.1f,.1f),new(.15f,.1f,.1f),new(0,1.1f,0),new(0,1.3f,0),new(0,1.45f,0),new(0,1.6f,0),new(0,1.8f,0),new(-.1f,1.5f,0),new(.1f,1.5f,0),new(-.4f,1.5f,0),new(.4f,1.5f,0),new(-.7f,1.5f,0),new(.7f,1.5f,0),new(-.9f,1.5f,0),new(.9f,1.5f,0),new(-.15f,.1f,.3f),new(.15f,.1f,.3f)];
        var nodes = new NativeHumanNode[23]; nodes[0] = new(-1, 0, "", Vector3.Zero, Quaternion.Identity, -1);
        for (int i = 0; i < 22; i++) nodes[i + 1] = new(parents[i] + 1, (uint)(i + 1), "root/bone" + i, parents[i] < 0 ? world[i] : world[i] - world[parents[i]], Quaternion.Identity, 0);
        var masses = new float[25]; masses[0] = 1;
        return new(nodes, [new(Quaternion.Identity, Quaternion.Identity, Vector3.One, -Vector3.One, Vector3.One)], [.. Enumerable.Range(1, 22), -1, -1, -1], masses, 1, new(1, 0, 1, 0));
    }
}

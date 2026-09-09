using System.Numerics;
using System.Text.Json;
using Sora.Core;
internal static class NativeNpcTests
{
    private static double[] Mat(Matrix4x4 m)=>[m.M11,m.M21,m.M31,m.M41,m.M12,m.M22,m.M32,m.M42,m.M13,m.M23,m.M33,m.M43,m.M14,m.M24,m.M34,m.M44];
    private static BoneRecord Bone(string path,int parent,Vector3 p,uint hash)=>new(path.Split('/')[^1],parent,[p.X,p.Y,p.Z],[p.X,p.Y+1,p.Z],RestMatrix:Mat(Matrix4x4.CreateTranslation(p)),SourcePath:path,SourceHash:hash);
    public static void Run(Action<string,Action> test,Action<Action> reject)
    {
        test("NPC explicit independent face and ear declarations",()=>{
            using var d=JsonDocument.Parse("""{"id":"fixture","avatarTempletName":"NPC/AvatarTemplet/x","avatarMeshName":"NPC/AvatarMesh/x","facialMorphAvatarName":"FacialMorph/Avatar/Girl/face","earMorphAvatarName":"EarMorph/Avatar/Boy/ear","partNameIdList":["fox_c"],"scale":1}""");
            var result=NativeNpcImport.Parse(d.RootElement);if(result.Ear!="EarMorph/Avatar/Boy/ear")throw new Exception("Ear derived from face or filename");
            var noEar=JsonSerializer.SerializeToNode(d.RootElement)!;noEar.AsObject().Remove("earMorphAvatarName");if(NativeNpcImport.Parse(JsonSerializer.SerializeToElement(noEar)).Ear is not null)throw new Exception("Absent ear declaration was fabricated");
        });
        test("NPC animation namespace admits independent part hash collisions and rejects true ambiguity",()=>{
            var scene=new SceneDocument("namespace",[Bone("Root/Head",-1,Vector3.Zero,77),Bone("@part/Head",-1,Vector3.Zero,77)],[],[],[]);
            var map=NativeNpcImport.AnimationBindings(scene,new Dictionary<uint,string>{{77,"Root/Head"}});
            if(map[77]!=0||scene.Bones[1].SourceHash!=77)throw new Exception("Part source identity was coerced or shadowed template");
            reject(()=>NativeNpcImport.AnimationBindings(scene with {Bones=[..scene.Bones,scene.Bones[0]]},new Dictionary<uint,string>{{77,"Root/Head"}}));
        });
        test("NPC shared source preserves nonapplicable CFG records and separate Avatar node index",()=>{
            using var host=JsonDocument.Parse("""{"avatar":{"m_FileID":0,"m_PathID":0},"data":{"basePoseConfig":{"Array":[{"boneID":357,"boneNameHash":42},{"boneID":999,"boneNameHash":99}]},"allBoneNames":{"Array":["earE","earC"]}}}""");
            var source=new NativeNpcMorphSource("EarMorph/Avatar/Girl/girl","ear.asset","CAB-source",1,true,host.RootElement.Clone(),[
                new(357,42,"earE","selected","CAB-part",2,11,"Head/earE",12,0,"Root/Head/earE"),
                new(999,99,"earC","not-applicable-to-selected-parts",null,null,null,null,null,null,null)]);
            var face=new FaceDriverRecord(1,"ear.asset","CAB-source",1,"CAB-part",2,"zyx",[1,-1,-1],0,[new(357,42,"Root/Head/earE",0,[0,0,0],[0,0,0],[1,1,1],11)],[],[],NativeSources:[source]);
            var partSource=new NativeNpcPartRecord("ear",1,"CAB-part",2,"Root/Head",[],[new(11,"Head/earE",12,0,"Root/Head/earE")],0,default,[]);
            var npc=new NativeNpcProvenance(null!,default,"template",1,"common",2,"table",3,[partSource],[]);
            var scene=new SceneDocument("shared",[Bone("Root/Head/earE",-1,Vector3.Zero,12)],[],[],[],Npc:npc);
            NativeFaceMorph.Validate(face,scene);
            NativeFaceMorph.Validate(face with {NativeSources=[source with {SourceCab="cab-SOURCE",Bones=[source.Bones[0] with {AvatarCab="cab-PART"},source.Bones[1]]}]},scene);
            foreach(var changed in new[]{source.Bones[0] with {AvatarCab="CAB-other"},source.Bones[0] with {AvatarObject=99},source.Bones[0] with {PathHash=99},source.Bones[0] with {NativePath="Other/earE"}})
                reject(()=>NativeFaceMorph.Validate(face with {NativeSources=[source with {Bones=[changed,source.Bones[1]]}]},scene));
            reject(()=>NativeFaceMorph.Validate(face,scene with {Npc=null}));
            // Coherent archival changes remain valid: this join is not source authentication.
            var coherentSource=source with {Bones=[source.Bones[0] with {PathHash=99},source.Bones[1]]};
            var coherentPart=partSource with {Bindings=[partSource.Bindings[0] with {NativePathHash=99}]};
            NativeFaceMorph.Validate(face with {NativeSources=[coherentSource]},scene with {Npc=npc with {Parts=[coherentPart]}});
            reject(()=>NativeFaceMorph.Validate(face with {Bones=[face.Bones[0] with {AvatarNodeIndex=357}]},scene));
            reject(()=>NativeFaceMorph.Validate(face with {NativeSources=[source with {Bones=[source.Bones[0]]}]},scene));
            reject(()=>NativeFaceMorph.Validate(face with {NativeSources=[source with {SourceAvatarNull=false}]},scene));
        });
        test("NPC SRED crosschecks mesh identity slot hash palette namespace and declaration ID",()=>{
            using var declaration=JsonDocument.Parse("""{"id":"fixture","avatarTempletName":"NPC/AvatarTemplet/x","avatarMeshName":"NPC/AvatarMesh/x","partNameIdList":["part"],"scale":1}""");
            using var slot=JsonDocument.Parse("""{"name":"part","parentBoneTransformName":"Root/Head","bUseSelfAvatar":1,"selfAvatarPathHash":20,"partSubMeshsLOD0":{"Array":[{"meshName":"mesh","meshPathHash":7}]}}""");
            using var binds=JsonDocument.Parse("""{"Array":[{}]}""");
            var meshSource=new NativeNpcMeshSource(7,"CAB-mesh",8,binds.RootElement.Clone(),[12]);
            // Native part head hash12 aliases template head hash77; this is valid.
            var binding=new NativeNpcBoneBinding(11,"Head",12,0,"Root/Head");
            var partSource=new NativeNpcPartRecord("part",20,"CAB-avatar",21,"Root/Head",["mesh"],[binding],0,slot.RootElement.Clone(),[meshSource]);
            var npc=new NativeNpcProvenance(new("VFS/block.blc",new("Data/Json/NPC/PrefabInfo/fixture.json","chunk.chk",0,1,false,0)),declaration.RootElement.Clone(),"CAB-template",1,"CAB-common",2,"CAB-table",3,[partSource],[]);
            var mesh=new MeshRecord("mesh",[[0,0,0]],[],[[0,1,0]],[[0,0]],0,[new(0,0,1)],[],SourceId:"CAB-mesh:8");
            var scene=new SceneDocument("fixture",[Bone("Root/Head",-1,Vector3.Zero,77)],[mesh],[new("material",[1,1,1,1],0,.5)],[],Npc:npc);
            void Parse(SceneDocument value)=>DatabaseFile.ParsePayload(JsonSerializer.SerializeToUtf8Bytes(new DatabaseDocument("fixture",[new("npc:fixture","fixture","test","character",[],value)]),WireJson.Options));
            Parse(scene);
            foreach(var field in new[]{"name","parentBoneTransformName","selfAvatarPathHash"})
            {
                var mismatch=JsonSerializer.SerializeToNode(slot.RootElement)!;
                mismatch[field]=field=="selfAvatarPathHash"?JsonSerializer.SerializeToNode(99):JsonSerializer.SerializeToNode("wrong");
                reject(()=>Parse(scene with {Npc=npc with {Parts=[partSource with {SourceSlot=JsonSerializer.SerializeToElement(mismatch)}]}}));
            }
            var secondMesh=mesh with {Name="mesh2",SourceId="CAB-mesh:9"};
            var twoSlot=JsonSerializer.SerializeToNode(slot.RootElement)!;
            twoSlot["partSubMeshsLOD0"]!["Array"]!.AsArray().Add(JsonSerializer.SerializeToNode(new {meshName="mesh2",meshPathHash=8}));
            var twoPart=partSource with {Meshes=["mesh","mesh2"],MeshSources=[meshSource,meshSource with {Hash=8,Object=9}],SourceSlot=JsonSerializer.SerializeToElement(twoSlot)};
            var twoNpc=npc with {Parts=[twoPart],NeutralMeshChanges=[new("mesh",0),new("mesh2",0)]};
            Parse(scene with {Meshes=[mesh,secondMesh],Npc=twoNpc});
            reject(()=>Parse(scene with {Meshes=[mesh,secondMesh],Npc=twoNpc with {NeutralMeshChanges=[new("mesh",0),new("mesh",0)]}}));
            reject(()=>Parse(scene with {Meshes=[mesh with {SourceId="CAB-wrong:8"}]}));
            reject(()=>Parse(scene with {Npc=npc with {Parts=[partSource with {MeshSources=[meshSource with {Hash=9}]}]}}));
            var alteredSlot=JsonSerializer.SerializeToNode(slot.RootElement)!;alteredSlot["partSubMeshsLOD0"]!["Array"]![0]!["meshName"]="other";
            reject(()=>Parse(scene with {Npc=npc with {Parts=[partSource with {SourceSlot=JsonSerializer.SerializeToElement(alteredSlot)}]}}));
            reject(()=>Parse(scene with {Npc=npc with {Parts=[partSource with {MeshSources=[meshSource with {BonePathHashes=[99]}]}]}}));
            reject(()=>Parse(scene with {Npc=npc with {Parts=[partSource with {Bindings=[binding with {AssembledPath="Wrong/Head"}]}]}}));
            var alteredDeclaration=JsonSerializer.SerializeToNode(declaration.RootElement)!;alteredDeclaration["id"]="other";
            reject(()=>Parse(scene with {Npc=npc with {Declaration=JsonSerializer.SerializeToElement(alteredDeclaration)}}));
        });
        var part=new SceneDocument("part",[Bone("",-1,Vector3.Zero,0),Bone("Head",0,new(3,0,0),1),Bone("Head/earE",1,new(4,0,0),2)],
            [new("ear",[[4,0,0]],[],[[0,1,0]],[[0,0]],0,[new(0,2,1)],[])],[],[]);
        test("NPC self root attachment preserves local mesh bind and native IDs",()=>{
            var target=new List<BoneRecord>{Bone("Root",-1,Vector3.Zero,3),Bone("Root/Head",0,new(0,7,0),4)};
            var records=NativeNpcImport.Assemble(target,part,"Root/Head","selected",out var meshes,out double error);
            var ear=records.Single(b=>b.NativePath=="Head/earE");if(ear.AvatarNodeIndex!=2||ear.AssembledPath!="Root/Head/selected/Head/earE"||error>1e-6||Vector3.Distance(new((float)meshes[0].Positions[0][0],(float)meshes[0].Positions[0][1],0),new(1,7,0))>1e-6)throw new Exception("Part was misplaced or source namespace lost");
            if(meshes[0].Weights[0].Bone!=ear.SceneBone)throw new Exception("Skin palette not remapped");
        });
        test("NPC self attachment retains the native part frame under rotated common head",()=>{
            var rotated=Matrix4x4.CreateRotationZ(MathF.PI/2)*Matrix4x4.CreateTranslation(0,7,0);
            var target=new List<BoneRecord>{Bone("Root",-1,Vector3.Zero,3),Bone("Root/Head",0,new(0,7,0),4) with {RestMatrix=Mat(rotated)}};
            var bindings=NativeNpcImport.Assemble(target,part,"Root/Head","selected",out var meshes,out double error);
            var anchor=bindings.Single(b=>b.NativePath=="Head");var root=bindings.Single(b=>b.NativePath=="");
            if(anchor.SceneBone==1||target[root.SceneBone].Parent!=1||Math.Abs(meshes[0].Positions[0][0]-1)>1e-6||Math.Abs(meshes[0].Positions[0][1]-7)>1e-6||error>1e-6)throw new Exception("Distinct native part frame collapsed into the common head frame");
        });
        test("NPC authored neutral rebase moves skin and rest together without mutating source",()=>{
            var face=new FaceDriverRecord(1,"face.asset","CAB-face",1,"CAB-avatar",2,"zyx",[1,-1,-1],0,[new(357,42,"Head/earE",2,[-2,0,0],[0,0,0],[1,1,1],11)],[new("ear",1,1,1,1,[new(0,[.5,0,0],[0,0,0],[0,0,0])])],[]);
            var input=part with {FaceDriver=face};string before=JsonSerializer.Serialize(input,WireJson.Options);
            var neutral=NativeNpcImport.ApplyAuthoredNeutral(input);
            if(Math.Abs(neutral.Meshes[0].Positions[0][0]-5)>1e-6||Math.Abs(neutral.Bones[2].Head[0]-5)>1e-6)throw new Exception("Authored skin/rest were not rebased together");
            if(before!=JsonSerializer.Serialize(input,WireJson.Options))throw new Exception("Native input mutated");
            var second=NativeNpcImport.ApplyAuthoredNeutral(neutral);if(Math.Abs(second.Meshes[0].Positions[0][0]-5)>1e-6)throw new Exception("Neutral application accumulates");
        });
        test("NPC duplicate full attachment and leaf guesses rejected",()=>{
            var target=new List<BoneRecord>{Bone("Root/Head",-1,Vector3.Zero,1),Bone("Other/Head",-1,Vector3.Zero,2)};
            reject(()=>NativeNpcImport.Assemble(target,part,"Head","selected",out _,out _));
            target.Add(target[0]);reject(()=>NativeNpcImport.Assemble(target,part,"Root/Head","selected",out _,out _));
        });
        test("NPC common binding uses template rest without adding unselected families",()=>{
            var target=new List<BoneRecord>{Bone("",-1,Vector3.Zero,0),Bone("Head",0,new(3,2,0),1)};
            var source=part with {Bones=[..part.Bones,Bone("Head/unselected",1,new(9,0,0),3)]};
            var result=NativeNpcImport.Assemble(target,source,null,"common",out var meshes,out _);
            if(result.Any(b=>b.NativePath=="Head/unselected")||Math.Abs(meshes[0].Positions[0][1]-2)>1e-6)throw new Exception("Unselected family added or new branch moved unexpectedly");
        });
    }
}

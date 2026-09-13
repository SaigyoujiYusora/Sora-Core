using System.Numerics;
using System.Text.Json;

namespace Sora.Core;

public sealed record NativeEquipmentGap(string Scope, string Code, string Message, bool BlocksBinding);
public sealed record NativeEquipmentState(bool Visible, bool CanBind, int? ParentBoneIndex, string? ParentSourcePath,
    string? AttachmentNodeId, string? AttachmentSourcePath, double[]? LocalMatrix, double[]? NativeMountWorldInScene,
    double[]? ParentBoneRestMatrix, string Status, string Space = "core-scene-relative-to-owner-bone-head-rest", string TargetRole = "owner", string TargetKind = "bone-head", string? TargetDedicatedSlotId = null, string? ParentNodeId = null, double[]? ParentNodeRestMatrix = null);
public sealed record NativeEquipmentClipInfo(string SourceId, string Name, JsonElement[] Events, uint[] BindingPaths, string? OriginalSourceId = null, string[]? ControllerChain = null, NativeWeaponAnimationEvent[]? DecodedWeaponEvents = null);
public sealed record NativeEquipmentControllerInfo(string AnimatorId, string? ControllerId, string Status, NativeEquipmentClipInfo[] Clips);
public sealed record NativeEquipmentResource(string ResourceId, string ResourcePath, SceneDocument? Scene, string NativeRig,
    string[] AlreadyPresentMeshIds, NativeEquipmentControllerInfo[] Controllers, string Status, NativeEquipmentDefaultPose? DefaultPose = null);
public sealed record NativeEquipmentBinding(string SlotId, int WeaponIndex, string? ResourceId, NativeEquipmentState Idle,
    NativeEquipmentState Fight, string Status);
public sealed record NativeEquipmentAnimationConfig(string Path, ResourceFileRecord Source, int Bytes, string Header, string Status, int? ControllerOffset = null, string? ControllerHash = null, string? ControllerPath = null, string? ControllerSourceId = null, NativeAnimationMaskReference[]? Masks = null, NativeEquipmentClipInfo[]? Clips = null);
public sealed record NativeEquipmentHelperDriver(string SourceId, string? TargetNodeId, string? TargetSourcePath, int? TargetBoneIndex,
    string? FinalNodeId, JsonElement NativeData, string Status);
public sealed record NativeEquipmentAssemblyData(string CharacterId, string OwnerResourcePath, string OwnerHierarchyRoot,
    NativeCharacterEquipmentData Declaration, NativeEquipmentResource[] Resources, NativeEquipmentBinding[] Slots,
    NativeEquipmentControllerInfo[] OwnerControllers, NativeEquipmentGap[] Gaps, Dictionary<string,bool> CanBindStates,
    string Status, string MatrixInvariant = "ownerBoneHeadRestMatrix @ localMatrix = nativeMountWorldInScene @ declaredScale; resource node matrices and Blender bone-parent tail offset are excluded",
    NativeEquipmentHelperDriver[]? HelperDrivers = null, string? PostmodelSourcePath = null, NativeEquipmentAnimationConfig? AnimationConfig = null, NativeEquipmentBinding[]? DynamicBindings = null);

/// <summary>Produces source-derived attachment frames, with unresolved states kept explicit.</summary>
public static class NativeEquipmentAssembly
{
    public static NativeEquipmentDefaultPose? ReadOptionalInitialPose(string resourcePath,Func<NativeEquipmentDefaultPose?> read,ICollection<NativeEquipmentGap> gaps)
    {
        try{return read();}
        catch(Exception error) when(error is InvalidDataException or IOException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or JsonException)
        {
            gaps.Add(new(resourcePath,"default-equipment-pose-unavailable",error.Message,false));return null;
        }
    }
    private static JsonElement Json(SerializedObject value)=>JsonSerializer.SerializeToElement(value.Data,WireJson.Options);
    private static JsonElement[] Array(JsonElement value,string field)=>value.GetProperty(field).GetProperty("Array").EnumerateArray().ToArray();
    private static double[] Values(Matrix4x4 m)=>[m.M11,m.M21,m.M31,m.M41,m.M12,m.M22,m.M32,m.M42,m.M13,m.M23,m.M33,m.M43,m.M14,m.M24,m.M34,m.M44];
    private static Matrix4x4 Matrix(double[] m)=>new((float)m[0],(float)m[4],(float)m[8],(float)m[12],(float)m[1],(float)m[5],(float)m[9],(float)m[13],(float)m[2],(float)m[6],(float)m[10],(float)m[14],(float)m[3],(float)m[7],(float)m[11],(float)m[15]);
    public static NativeEquipmentAssemblyData Resolve(GameResources game,SceneDocument owner,string ownerPath,bool includeDynamic = false)
    {
        Validation.Scene(owner);
        string character=Path.GetFileNameWithoutExtension(ownerPath).Replace("_uimodel","",StringComparison.Ordinal);
        var declaration=NativeCharacterEquipment.Read(game,character);
        NativeMountTarget DynamicMount(JsonElement value,string mode) {
            var target=value.GetProperty(mode+"MountTarget");int mount=target.GetProperty("characterMountPoint").GetInt32(),legacy=value.GetProperty("legacy"+char.ToUpperInvariant(mode[0])+mode[1..]+"MountPoint").GetInt32();
            return new(target.GetProperty("mountToWeapon").GetInt32()!=0,mount,declaration.CharacterMountPoints.GetValueOrDefault(mount),target.GetProperty("targetWeaponIndex").GetInt32(),target.GetProperty("weaponMountPoint").GetInt32(),legacy,declaration.CharacterMountPoints.GetValueOrDefault(legacy),"native-dynamic-declaration");
        }
        var dynamicSlots=includeDynamic?declaration.DynamicWeapons.Select(value=>new NativeEquipmentSlot(character+":weapon:"+value.GetProperty("weaponIndex").GetInt32(),value.GetProperty("weaponIndex").GetInt32(),"native-dynamic","",null,value.GetProperty("weaponScale").GetDouble(),value.GetProperty("showWhenIdle").GetInt32()!=0,value.GetProperty("showWhenFight").GetInt32()!=0,DynamicMount(value,"idle"),DynamicMount(value,"fight"),"native-dynamic-declaration",value)).ToArray():[];
        var hierarchy=NativePrefabHierarchy.Read(game,ownerPath);
        var gaps=new List<NativeEquipmentGap>();var resources=new List<NativeEquipmentResource>();
        var reflection=Matrix4x4.CreateScale(-1,1,1);var space=reflection*Matrix4x4.CreateRotationX(MathF.PI/2);Matrix4x4.Invert(space,out var inverseSpace);
        var rootBone=owner.Bones.Select((bone,index)=>(bone,index)).FirstOrDefault(p=>!string.IsNullOrEmpty(p.bone.SourcePath)&&!p.bone.SourcePath.Contains('/'));
        Validation.Require(rootBone.bone is not null&&rootBone.bone.RestMatrix is not null,"Owner Avatar root frame is missing");
        var roots=hierarchy.Where(node=>node.SourcePath.EndsWith('/'+rootBone.bone!.SourcePath,StringComparison.Ordinal)||node.SourcePath==rootBone.bone!.SourcePath).ToArray();
        Validation.Require(roots.Length==1&&Matrix4x4.Invert(roots[0].World,out _),"Owner native root cannot be aligned to imported Avatar");
        Matrix4x4.Invert(roots[0].World,out var inverseRoot);
        var alignment=inverseRoot*reflection*Matrix(rootBone.bone!.RestMatrix!)*inverseSpace;
        string prefix=roots[0].SourcePath[..^rootBone.bone.SourcePath!.Length];
        NativeHierarchyNode[]? postHierarchy=null;string? postPath=null;Matrix4x4 postAlignment=Matrix4x4.Identity;string? postPrefix=null;
        var requiredNames=declaration.DedicatedEquipment.Concat(dynamicSlots).SelectMany(slot=>new[]{slot.IdleMount.CharacterNodeName,slot.IdleMount.LegacyNodeName,slot.FightMount.CharacterNodeName,slot.FightMount.LegacyNodeName}).Where(name=>!string.IsNullOrEmpty(name)).Distinct().ToArray();
        if(requiredNames.Any(name=>!hierarchy.Any(node=>node.Name==name))) {
            try {
                postPath="assets/beyond/dynamicassets/gameplay/actors/postmodels/characters/"+declaration.ModelId+".prefab";
                postHierarchy=NativePrefabHierarchy.Read(game,postPath);
                Validation.Require(postHierarchy[0].Name==declaration.ModelId,"Native postmodel root disagrees with declaration modelId");
                var postRoots=postHierarchy.Where(node=>node.SourcePath.EndsWith('/'+rootBone.bone!.SourcePath,StringComparison.Ordinal)||node.SourcePath==rootBone.bone!.SourcePath).ToArray();
                Validation.Require(postRoots.Length==1&&Matrix4x4.Invert(postRoots[0].World,out _),"Native postmodel root cannot be aligned");
                Matrix4x4.Invert(postRoots[0].World,out var inversePostRoot);postAlignment=inversePostRoot*reflection*Matrix(rootBone.bone!.RestMatrix!)*inverseSpace;
                postPrefix=postRoots[0].SourcePath[..^rootBone.bone.SourcePath!.Length];
            } catch(Exception error) when(error is IOException or KeyNotFoundException or InvalidOperationException) {postHierarchy=null;gaps.Add(new(character,"postmodel-helper-unavailable",error.Message,false));}
        }
        var ownerMeshes=owner.Meshes.Where(mesh=>mesh.SourceId is not null).Select(mesh=>mesh.SourceId!).ToHashSet(StringComparer.Ordinal);
        var bonePaths=owner.Bones.Select((bone,index)=>(bone,index)).Where(p=>p.bone.SourcePath is not null).ToDictionary(p=>p.bone.SourcePath!,p=>p.index,StringComparer.Ordinal);
        NativeEquipmentControllerInfo[] Controllers(NativeHierarchyNode[] nodes,string scope) {
            try{return ReadControllers(game,nodes);}
            catch(Exception error) when(error is InvalidDataException or IOException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or JsonException) {
                gaps.Add(new(scope,"animation-metadata-unavailable",error.Message,false));return [];
            }
        }
        foreach(var item in declaration.DedicatedEquipment.DistinctBy(slot=>slot.ResourcePath)) {
            OperationProgress.Report("decode-dedicated-equipment",resources.Count,declaration.DedicatedEquipment.Select(slot=>slot.ResourcePath).Distinct().Count(),item.ResourcePath);
            if(item.ResourceId is null){gaps.Add(new(item.ResourcePath,"missing-resource","Authored equipment address is missing or ambiguous",true));continue;}
            try {
                var parsed=NativeItemImport.Import(game,item.ResourcePath);var scene=parsed.Assets.Single().Scene!;
                var overlap=scene.Meshes.Select(mesh=>mesh.SourceId!).Where(ownerMeshes.Contains).Distinct().ToArray();
                if(overlap.Length>0)gaps.Add(new(item.ResourcePath,"existing-mesh-instance-ambiguity","Owner already contains equipment mesh sources; renderer-instance ownership must be resolved before cloning",true));
                var itemNodes=NativePrefabHierarchy.Read(game,item.ResourcePath);
                var defaultPose=ReadOptionalInitialPose(item.ResourcePath,()=>NativeEquipmentDefaultPoseReader.Read(game,itemNodes,scene,item.ResourceId,item.ResourcePath),gaps);
                var controllers=Controllers(itemNodes,item.ResourcePath);
                if(defaultPose is not null&&controllers.Count(c=>c.AnimatorId==defaultPose.AnimatorId&&c.ControllerId==defaultPose.ControllerId&&c.Clips.Count(clip=>clip.SourceId==defaultPose.ClipId&&clip.Name==defaultPose.ClipName)==1)!=1)
                {
                    gaps.Add(new(item.ResourcePath,"default-equipment-pose-unavailable","Initial pose cannot be joined to exact controller/clip metadata",false));defaultPose=null;
                }
                resources.Add(new(item.ResourceId,item.ResourcePath,scene,scene.Bones.Length==0?"native-rigid-no-skeleton":"native-renderer-bones-and-bindposes",overlap,
                    controllers,overlap.Length==0?"decoded":"existing-mesh-instance-ambiguity",defaultPose));
            }catch(Exception error) when(error is IOException or KeyNotFoundException or InvalidOperationException) {
                gaps.Add(new(item.ResourcePath,"resource-parse-failed",error.Message,true));resources.Add(new(item.ResourceId,item.ResourcePath,null,"unresolved",[],[],"failed"));
            }
        }
        var bindings=new List<NativeEquipmentBinding>();
        NativeEquipmentState State(NativeEquipmentSlot slot,NativeMountTarget target,bool visible,string state,bool dynamic=false) {
            Validation.Require(double.IsFinite(slot.Scale)&&slot.Scale>0&&slot.Scale<=100,"Invalid declared equipment scale");
            NativeEquipmentState Unbound(string status,bool blocks) {
                gaps.Add(new(slot.SlotId+":"+state,status,visible?"Visible equipment has no supported unambiguous attachment":"Hidden equipment attachment is deferred to authored events",blocks));
                return new(visible,false,null,null,null,null,null,null,null,status);
            }
            if(target.MountToWeapon) {
                var parentSlot=declaration.DedicatedEquipment.SingleOrDefault(item=>item.WeaponIndex==target.TargetWeaponIndex);
                var resource=parentSlot is null?null:resources.SingleOrDefault(item=>item.ResourceId==parentSlot.ResourceId);
                if(parentSlot is null||resource?.Scene is null)return Unbound("missing-dedicated-parent",visible) with{TargetRole="dedicated"};
                var itemHierarchy=NativePrefabHierarchy.Read(game,parentSlot.ResourcePath);var points=new List<ResolvedAsset>();
                foreach(var node in itemHierarchy)foreach(var pointer in Array(Json(node.GameObject.Object),"m_Component")) {
                    var component=game.Resolve(node.GameObject.Cab,pointer.GetProperty("component"));if(component?.Object.ClassId!=114)continue;
                    var data=NativeObjectProjection.Json(component.Object,"weaponMountPoints");if(!data.TryGetProperty("weaponMountPoints",out var map))continue;
                    var keys=Array(map,"_keyData");var values=Array(map,"_valueData");Validation.Require(keys.Length==values.Length,"Weapon mount map arrays disagree");
                    for(int i=0;i<keys.Length;i++)if(keys[i].GetInt32()==target.WeaponMountPoint){var point=game.Resolve(component.Cab,values[i]);if(point is not null)points.Add(point);}
                }
                points=points.DistinctBy(NativePrefabHierarchy.Identity).ToList();
                if(points.Count!=1)return Unbound("missing-or-ambiguous-weapon-mount",visible) with{TargetRole="dedicated",TargetDedicatedSlotId=parentSlot.SlotId};
                var targetNode=itemHierarchy.SingleOrDefault(node=>NativePrefabHierarchy.Identity(node.Transform)==NativePrefabHierarchy.Identity(points[0]));
                if(targetNode is null)return Unbound("weapon-mount-outside-prefab",visible) with{TargetRole="dedicated",TargetDedicatedSlotId=parentSlot.SlotId};
                var weaponTargetWorld=inverseSpace*targetNode.World*space;
                var boneCandidates=resource.Scene.Bones.Select((bone,index)=>(bone,index)).Where(pair=>pair.bone.SourcePath is not null&&(targetNode.SourcePath==pair.bone.SourcePath||targetNode.SourcePath.StartsWith(pair.bone.SourcePath+"/",StringComparison.Ordinal))).OrderByDescending(pair=>pair.bone.SourcePath!.Length).ToArray();
                if(boneCandidates.Length>0) {
                    var parent=boneCandidates[0];if(boneCandidates.Count(pair=>pair.bone.SourcePath!.Length==parent.bone.SourcePath!.Length)!=1||parent.bone.RestMatrix is null)return Unbound("ambiguous-dedicated-parent-bone",visible);
                    if(!Matrix4x4.Invert(Matrix(parent.bone.RestMatrix),out var inverseParent))return Unbound("singular-dedicated-parent-bone",visible);
                    var weaponLocal=Matrix4x4.CreateScale((float)slot.Scale)*weaponTargetWorld*inverseParent;
                    return new(visible,true,parent.index,parent.bone.SourcePath,NativePrefabHierarchy.Identity(targetNode.Transform),targetNode.SourcePath,Values(weaponLocal),Values(weaponTargetWorld),parent.bone.RestMatrix,"native-weapon-mount-point","core-resource-scene-relative-to-dedicated-bone-head-rest","dedicated","bone-head",parentSlot.SlotId);
                }
                return new(visible,true,null,targetNode.SourcePath,NativePrefabHierarchy.Identity(targetNode.Transform),targetNode.SourcePath,Values(Matrix4x4.CreateScale((float)slot.Scale)),Values(weaponTargetWorld),null,"native-weapon-mount-point","core-resource-scene-relative-to-dedicated-node","dedicated","scene-node",parentSlot.SlotId,NativePrefabHierarchy.Identity(targetNode.Transform),Values(weaponTargetWorld));
            }
            int mount=target.CharacterMountPoint;string? name=target.CharacterNodeName;string status="native-mount-field";
            if(mount==0&&target.LegacyMountPoint>0){mount=target.LegacyMountPoint;name=target.LegacyNodeName;status="explicit-legacy-field";}
            // AbilitySystemUtils.GetNodeTransform treats mountPoint 0 as AbilitySystem.battleRoot: with
            // overrideBattleRoot=false that is the loaded main model prefab root transform, and
            // _AppearOnNodeUnsafe parents the weapon under it with worldPositionStays=false, so the weapon's
            // own local transform is preserved. A zero mount therefore needs no mountPointData entry and is
            // never reported as a missing mount.
            if(mount==0&&string.IsNullOrEmpty(name)&&target.LegacyMountPoint<=0) {
                var ownerRoots=hierarchy.Where(node=>!node.SourcePath.Contains('/')).ToArray();
                if(ownerRoots.Length!=1)return Unbound("ambiguous-owner-main-model-root",visible) with{TargetRole=dynamic?"owner":"dedicated",TargetDedicatedSlotId=dynamic?null:slot.SlotId};
                var ownerRoot=ownerRoots[0];var rootWorld=inverseSpace*ownerRoot.World*space;
                // A dynamic general-weapon slot mounting at declared point 0 belongs to the owner instance root; it is
                // not dedicated-equipment ownership, so it must not point at its own generic slot id (dedicated is unchanged).
                return new(visible,true,null,ownerRoot.SourcePath,NativePrefabHierarchy.Identity(ownerRoot.Transform),ownerRoot.SourcePath,
                    Values(Matrix4x4.CreateScale((float)slot.Scale)),Values(rootWorld),Values(rootWorld),"native-battle-root-mount",
                    "core-scene-relative-to-owner-main-model-root",dynamic?"owner":"dedicated","owner-main-model-root",dynamic?null:slot.SlotId,
                    NativePrefabHierarchy.Identity(ownerRoot.Transform),Values(rootWorld));
            }
            if(mount<=0||string.IsNullOrEmpty(name))return Unbound(visible?"missing-mount":"hidden-event-driven",visible);
            var activeHierarchy=hierarchy;var activeAlignment=alignment;string activePrefix=prefix;
            var matches=activeHierarchy.Select((node,index)=>(node,index)).Where(p=>p.node.Name==name).ToArray();
            if(matches.Length==0&&postHierarchy is not null) {
                activeHierarchy=postHierarchy;activeAlignment=postAlignment;activePrefix=postPrefix!;
                matches=activeHierarchy.Select((node,index)=>(node,index)).Where(p=>p.node.Name==name).ToArray();status+="+native-postmodel-static-helper";
            }
            if(matches.Length!=1)return Unbound(matches.Length==0?"missing-native-mount":"ambiguous-native-mount",visible);
            var selected=matches[0];int nodeIndex=selected.index;int? parentBone=null;var intermediate=new List<NativeHierarchyNode>();
            for(int current=nodeIndex;current>=0;current=activeHierarchy[current].Parent) {
                var node=activeHierarchy[current];
                if(node.SourcePath.StartsWith(activePrefix,StringComparison.Ordinal)&&bonePaths.TryGetValue(node.SourcePath[activePrefix.Length..],out int mapped)){parentBone=mapped;break;}
                intermediate.Add(node);
            }
            if(parentBone is null)return Unbound("mount-has-no-imported-bone-ancestor",visible);
            foreach(var helper in intermediate) {
                var components=Array(Json(helper.GameObject.Object),"m_Component").Select(pointer=>game.Resolve(helper.GameObject.Cab,pointer.GetProperty("component"))).ToArray();
                if(components.Any(component=>component is null||component.Object.ClassId!=4))return Unbound("helper-transform-component-unverified",visible);
            }
            var bone=owner.Bones[parentBone.Value];if(bone.RestMatrix is null)return Unbound("missing-parent-rest-matrix",visible);
            var rest=Matrix(bone.RestMatrix);if(!Matrix4x4.Invert(rest,out var inverseRest))return Unbound("singular-parent-rest-matrix",visible);
            Validation.Require(double.IsFinite(slot.Scale)&&slot.Scale>0&&slot.Scale<=100,"Invalid declared equipment scale");
            var targetWorld=inverseSpace*(selected.node.World*activeAlignment)*space;
            var local=Matrix4x4.CreateScale((float)slot.Scale)*targetWorld*inverseRest;
            var reconstructed=local*rest;var expected=Matrix4x4.CreateScale((float)slot.Scale)*targetWorld;
            Validation.Require(Values(reconstructed).Zip(Values(expected),(a,b)=>Math.Abs(a-b)).Max()<0.001,"Attachment frame invariant failed");
            return new(visible,true,parentBone,bone.SourcePath,NativePrefabHierarchy.Identity(selected.node.Transform),selected.node.SourcePath,Values(local),Values(targetWorld),bone.RestMatrix,status);
        }
        foreach(var slot in declaration.DedicatedEquipment) {
            var idle=State(slot,slot.IdleMount,slot.ShowWhenIdle,"idle");var fight=State(slot,slot.FightMount,slot.ShowWhenFight,"fight");
            bindings.Add(new(slot.SlotId,slot.WeaponIndex,slot.ResourceId,idle,fight,(idle.Visible&&!idle.CanBind)||(fight.Visible&&!fight.CanBind)?"blocked":"native-static-states-ready"));
        }
        var dynamicBindings=new List<NativeEquipmentBinding>();
        foreach(var slot in dynamicSlots) {
            var idle=State(slot,slot.IdleMount,slot.ShowWhenIdle,"idle",true);var fight=State(slot,slot.FightMount,slot.ShowWhenFight,"fight",true);
            dynamicBindings.Add(new(slot.SlotId,slot.WeaponIndex,null,idle,fight,(idle.Visible&&!idle.CanBind)||(fight.Visible&&!fight.CanBind)?"blocked":"native-static-states-ready"));
        }
        bool geometry=resources.Count==declaration.DedicatedEquipment.Select(slot=>slot.ResourcePath).Distinct().Count()&&resources.All(resource=>resource.Status=="decoded");
        var ready=new Dictionary<string,bool>{{"idle",geometry&&bindings.All(slot=>!slot.Idle.Visible||slot.Idle.CanBind)},{"fight",geometry&&bindings.All(slot=>!slot.Fight.Visible||slot.Fight.CanBind)}};
        var drivers=new List<NativeEquipmentHelperDriver>();
        if(postHierarchy is not null) {
            var byId=postHierarchy.ToDictionary(node=>NativePrefabHierarchy.Identity(node.Transform),StringComparer.Ordinal);var driverSeen=new HashSet<string>();
            foreach(var node in postHierarchy) foreach(var pointer in Array(Json(node.GameObject.Object),"m_Component")) {
                var component=game.Resolve(node.GameObject.Cab,pointer.GetProperty("component"));if(component is null||component.Object.ClassId!=114||!driverSeen.Add(NativePrefabHierarchy.Identity(component)))continue;
                var data=Json(component.Object);if(!data.TryGetProperty("dampedTransform",out _)||!data.TryGetProperty("finalTransform",out var finalPointer))continue;
                var target=game.Resolve(component.Cab,data.GetProperty("target"));var final=game.Resolve(component.Cab,finalPointer);
                string? targetId=target is null?null:NativePrefabHierarchy.Identity(target);string? targetPath=targetId is null?null:byId.GetValueOrDefault(targetId)?.SourcePath;
                int? targetBone=targetPath is not null&&targetPath.StartsWith(postPrefix!,StringComparison.Ordinal)&&bonePaths.TryGetValue(targetPath[postPrefix!.Length..],out int index)?index:null;
                drivers.Add(new(NativePrefabHierarchy.Identity(component),targetId,targetPath,targetBone,final is null?null:NativePrefabHierarchy.Identity(final),data.Clone(),"native-damping-parameters-unapplied"));
            }
            if(drivers.Count>0)gaps.Add(new(character,"native-helper-damping-unapplied","Postmodel helper default transforms are bindable; authored damping and compensation parameters are preserved but not evaluated",false));
        }
        NativeEquipmentAnimationConfig? animationConfig=null;
        if(!string.IsNullOrEmpty(declaration.AnimationConfigPath)) {
            try {
                var bytes=game.GetBytes(declaration.AnimationConfigPath);
                var header=NativeAnimationConfig.ReadController(game,declaration.AnimationConfigPath);
                var clips=DescribeClips(game,header.Controller).Select(clip=>clip with {DecodedWeaponEvents=NativeWeaponAnimationEvents.Decode(clip,declaration)}).ToArray();
                animationConfig=new(declaration.AnimationConfigPath,header.Source,bytes.Length,Convert.ToHexString(bytes.AsSpan(0,Math.Min(bytes.Length,16))),"controller-prefix-resolved-remaining-config-bytes-unparsed",header.ControllerOffset,header.ControllerHash,header.ControllerPath,NativePrefabHierarchy.Identity(header.Controller),header.Masks,clips);
                gaps.Add(new(character,"native-animation-state-commands-unparsed","Authored controller/mask hashes, effective clip references and decoded WeaponVisible/WeaponAnim events are resolved; the remaining animation-config bytes are not decoded",false));
            }catch(Exception error) when(error is IOException or KeyNotFoundException) {gaps.Add(new(character,"native-animation-config-unavailable",error.Message,false));}
        }
        var ownerControllers=Controllers(hierarchy,ownerPath);
        gaps.Add(new(character,"animation-events-not-applied","Authored idle/fight declarations and decoded WeaponVisible/WeaponAnim events are delivered to the Blender front-end, which applies slot visibility and idle/fight mounts per body clip; Core does not evaluate them, and hide effects, damping and clips without an authored visibility event stay explicit",false));
        return new(character,ownerPath,NativePrefabHierarchy.Identity(hierarchy[0].Transform),declaration,resources.ToArray(),bindings.ToArray(),ownerControllers,gaps.ToArray(),ready,
            ready.Values.All(value=>value)?"static-states-ready-animation-unverified":"blocked-required-equipment",HelperDrivers:drivers.ToArray(),PostmodelSourcePath:postPath,AnimationConfig:animationConfig,DynamicBindings:dynamicBindings.ToArray());
    }
    private static NativeEquipmentClipInfo[] DescribeClips(GameResources game,ResolvedAsset controller)
    {
        var clips=new List<NativeEquipmentClipInfo>();var references=NativeAnimationController.Read(game,controller);
        foreach(var reference in references) {
            OperationProgress.Report("read-equipment-animation-links",clips.Count,references.Length);
            var clip=reference.Clip;var data=NativeObjectProjection.Json(clip.Object,"m_Name","m_Events","m_ClipBindingConstant");
            var events=data.TryGetProperty("m_Events",out var eventData)?eventData.GetProperty("Array").EnumerateArray().Select(value=>value.Clone()).ToArray():[];
            uint[] paths=data.TryGetProperty("m_ClipBindingConstant",out var bindings)&&bindings.TryGetProperty("genericBindings",out var generic)?generic.GetProperty("Array").EnumerateArray().Select(binding=>binding.GetProperty("path").GetUInt32()).Distinct().ToArray():[];
            clips.Add(new(NativePrefabHierarchy.Identity(clip),data.GetProperty("m_Name").GetString()!,events,paths,reference.OriginalSourceId,reference.ControllerChain));
        }
        return clips.ToArray();
    }
    private static NativeEquipmentControllerInfo[] ReadControllers(GameResources game,NativeHierarchyNode[] hierarchy)
    {
        var controllers=new List<NativeEquipmentControllerInfo>();var seen=new HashSet<string>();
        foreach(var node in hierarchy) foreach(var component in Array(Json(node.GameObject.Object),"m_Component")) {
            var animator=game.Resolve(node.GameObject.Cab,component.GetProperty("component"));if(animator is null||animator.Object.ClassId!=95)continue;
            if(!seen.Add(NativePrefabHierarchy.Identity(animator)))continue;
            var controller=game.Resolve(animator.Cab,Json(animator.Object).GetProperty("m_Controller"));
            if(controller is null){controllers.Add(new(NativePrefabHierarchy.Identity(animator),null,"no-controller",[]));continue;}
            var clips=DescribeClips(game,controller);
            controllers.Add(new(NativePrefabHierarchy.Identity(animator),NativePrefabHierarchy.Identity(controller),"native-clips-and-events-unapplied",clips));
        }
        return controllers.ToArray();
    }
}

using System.Collections.ObjectModel;
using System.Numerics;
using System.Text.Json;

namespace Sora.Core;

/// <summary>Explicit binding plan for the observed Endfield ACL transform/Animator layout.</summary>
public sealed class NativeAnimationBinding
{
    private readonly float[] values;
    private readonly Dictionary<uint, int> scalarColumns;
    public ReadOnlyCollection<uint> TransformPaths { get; }
    public ReadOnlyCollection<bool> PositionTracks { get; }
    public ReadOnlyCollection<bool> RotationTracks { get; }
    public ReadOnlyCollection<bool> ScaleTracks { get; }
    public ReadOnlyCollection<uint> CustomScalarAttributes { get; }
    public int Samples { get; }
    public int Scalars { get; }
    public float SampleRate { get; }
    public bool RootScalarsValidated { get; }
    /// <summary>True for the classic single-stream storage, which has no separate root buffer to agree with.</summary>
    public bool NonAclSingleStream { get; }
    /// <summary>Storage notes of the classic single-stream decode (empty for ACL storage).</summary>
    public ReadOnlyCollection<string> StorageNotes { get; } = Array.AsReadOnly(Array.Empty<string>());
    public bool HasHumanoid { get; }
    public float RootComparisonMaximumError { get; }
    /// <summary>Authored clip interval. The decoded sample grid is not always aligned to it, so the authored end is
    /// kept and the final decoded sample value is held to it instead of shortening the clip to the grid.</summary>
    public double Duration { get; }
    /// <summary>True when the authored end lies inside the final sample interval beyond the last decoded sample.</summary>
    public bool AuthoredEndBeyondGrid { get; }

    /// <summary>Observed native relation: the authored end may sit up to one sample interval past the last decoded
    /// sample (walk loop: 65 samples at 60 Hz, grid 1.0666667, authored 1.0709809). The grid must cover the authored
    /// interval, no key is fabricated and no authored duration is discarded.</summary>
    private static double AuthoredInterval(JsonElement muscle, int samples, float rate, out bool beyondGrid)
    {
        double stop = muscle.GetProperty("m_StopTime").GetDouble();
        double grid = (samples - 1) / (double)rate;
        Validation.Require(muscle.GetProperty("m_StartTime").GetDouble() == 0 && stop > 0
            && stop >= grid - 0.0001 && stop <= grid + 1.0 / rate + 0.0001, "Unsupported native animation time interval");
        // Float32 authored ends carry a few ULPs of noise above the decoded grid (idle_to_battle 4.666667 vs
        // 280/60 = 4.6666666667). Only a real sub-sample tail extends the duration; noise keeps the exact grid
        // value so the frame count never gains a whole extra frame.
        beyondGrid = stop > grid + 0.0001;
        return beyondGrid ? stop : grid;
    }

    public NativeAnimationBinding(JsonElement clip, AclSamples samples, AclSamples? rootSamples = null)
    {
        Validation.Require(samples is not null && samples.Tracks is > 0 and <= 4096 && samples.Scalars is >= 14 and <= 4096 && samples.Samples is > 0 and <= 1000000 && float.IsFinite(samples.SampleRate) && samples.SampleRate > 0, "Unsupported animation sample layout");
        long length = (samples!.Tracks * 10L + samples.Scalars) * samples.Samples;
        Validation.Require(length <= 8 * 1024 * 1024 && samples.Values is not null && samples.Values.Length == length && samples.Values.All(float.IsFinite), "Invalid animation sample values");
        var buffer = clip.GetProperty("m_AclCompressedBuffer");
        var muscle = clip.GetProperty("m_MuscleClip");
        Validation.Require(NativeHumanoidRig.ArrayField(muscle.GetProperty("m_DeltaPose"), "m_DoFArray", 61).Length == 61, "Clip does not use the observed native61 muscle layout");
        Validation.Require(clip.GetProperty("m_SampleRate").GetSingle() == samples.SampleRate, "Native clip and decoded sample rates differ");
        Duration = AuthoredInterval(muscle, samples.Samples, samples.SampleRate, out bool beyondGrid);
        AuthoredEndBeyondGrid = beyondGrid;
        Validation.Require(clip.GetProperty("m_aclType").GetInt32() == 16 && buffer.GetProperty("Header").GetProperty("Version").GetInt32() == 10, "Unsupported Endfield animation encoding");
        Validation.Require(buffer.GetProperty("OutputTrackCount").GetInt32() == samples.Tracks && buffer.GetProperty("FloatCurveCount").GetInt32() == samples.Scalars, "Native binding and ACL track counts differ");
        Validation.Require(buffer.GetProperty("RootScaleIndex").GetInt32() == 65535, "Unsupported native root-scale binding mode");
        Validation.Require(NativeHumanoidRig.ArrayField(buffer, "m_DefaultIndexs", 16384).Length == 0 && Convert.FromBase64String(buffer.GetProperty("TransformSubTrackConstantMasks").GetProperty("Array").GetString()!).Length == 0, "Nonempty native default/constant masks require another binding implementation");
        var bindings = NativeHumanoidRig.ArrayField(clip.GetProperty("m_ClipBindingConstant"), "genericBindings", 16384);
        var position = new List<uint>(); var rotation = new List<uint>(); var scale = new List<uint>(); scalarColumns = [];
        foreach (var binding in bindings)
        {
            int type = binding.GetProperty("typeID").GetInt32(); uint attribute = binding.GetProperty("attribute").GetUInt32();
            int custom = binding.GetProperty("customType").GetInt32();
            Validation.Require(binding.GetProperty("isPPtrCurve").GetInt32() == 0, "Unsupported object animation binding");
            if (type == 4)
            {
                Validation.Require(custom == 0, "Unsupported Transform custom binding");
                uint path = binding.GetProperty("path").GetUInt32();
                if (attribute == 1) position.Add(path); else if (attribute == 2) rotation.Add(path); else if (attribute == 3) scale.Add(path); else throw new InvalidDataException("Unsupported Transform animation channel");
            }
            else if (type == 95)
            {
                Validation.Require(binding.GetProperty("path").GetUInt32() == 0 && custom == (attribute < 143 ? 8 : 0), "Unsupported Animator scalar binding kind");
                Validation.Require(scalarColumns.TryAdd(attribute, scalarColumns.Count), "Duplicate Animator scalar attribute");
            }
            else throw new InvalidDataException("Unsupported native animation binding type");
        }
        Validation.Require(position.Distinct().Count() == position.Count && rotation.Distinct().Count() == rotation.Count && scale.Distinct().Count() == scale.Count, "Duplicate Transform channel binding");
        HasHumanoid=scalarColumns.Keys.Any(x=>x>=14&&x<143);
        Validation.Require(scalarColumns.Count == samples.Scalars && Enumerable.Range(0, HasHumanoid?143:14).All(x => scalarColumns.ContainsKey((uint)x)), "Required native scalar attributes are absent");
        int rootTracks=HasHumanoid?28:21;
        Validation.Require(buffer.GetProperty("RootPosIndex").GetInt32()==(HasHumanoid?65535:0)&&buffer.GetProperty("RootRotIndex").GetInt32()==(HasHumanoid?65535:0)&&buffer.GetProperty("RootTrackCount").GetInt32()==rootTracks,"Unsupported native root-track binding mode");
        string maskText=buffer.GetProperty("TransformSubTrackMasks").GetProperty("Array").GetString()!;int bytes = (samples.Tracks + 7) / 8;
        int paddedBytes=(bytes*3+7)/8*8;
        Validation.Require(maskText.Length<=((paddedBytes+2)/3)*4,"Oversized Transform mask");byte[] mask = Convert.FromBase64String(maskText);
        Validation.Require(mask.Length == paddedBytes, "Invalid Transform mask size");
        Validation.Require(mask.AsSpan(bytes*3).IndexOfAnyExcept((byte)0)<0,"Nonzero Transform mask alignment padding");
        if(samples.Tracks%8!=0)for(int channel=0;channel<3;channel++)Validation.Require((mask[(channel+1)*bytes-1]&((1<<(8-samples.Tracks%8))-1))==0,"Nonzero Transform mask padding");
        bool Has(int track, int channel) => (mask[channel * bytes + track / 8] & (128 >> (track % 8))) != 0;
        var paths=new uint?[samples.Tracks];
        void Assign(List<uint> channel,int slot)
        {
            int cursor=0;for(int track=0;track<samples.Tracks;track++)if(Has(track,slot))
            {Validation.Require(cursor<channel.Count,"Transform mask has more tracks than bindings");uint path=channel[cursor++];Validation.Require(paths[track] is null||paths[track]==path,"Transform mask and channel path order disagree");paths[track]=path;}
            Validation.Require(cursor==channel.Count,"Transform channel exceeds mask coverage");
        }
        Assign(position,0);Assign(rotation,1);Assign(scale,2);
        Validation.Require(paths.All(x=>x.HasValue)&&paths.Distinct().Count()==samples.Tracks,"Unidentified or ambiguous native transform track");
        TransformPaths = Array.AsReadOnly(paths.Select(x=>x!.Value).ToArray());
        PositionTracks=Array.AsReadOnly(Enumerable.Range(0,samples.Tracks).Select(i=>Has(i,0)).ToArray());RotationTracks=Array.AsReadOnly(Enumerable.Range(0,samples.Tracks).Select(i=>Has(i,1)).ToArray());ScaleTracks = Array.AsReadOnly(Enumerable.Range(0, samples.Tracks).Select(i => Has(i, 2)).ToArray());
        CustomScalarAttributes = Array.AsReadOnly(scalarColumns.Keys.Where(x => x >= 143).ToArray()); Samples = samples.Samples; Scalars = samples.Scalars; SampleRate = samples.SampleRate; values = (float[])samples.Values!.Clone();
        var constantIndices=NativeHumanoidRig.ArrayField(buffer,"m_ConstantIndexs",16384);var constants=NativeHumanoidRig.ArrayField(buffer,"m_ConstantValues",65536).Select(x=>x.GetSingle()).ToArray();
        Validation.Require(constants.All(float.IsFinite),"Nonfinite native animation constant");var usedConstants=new HashSet<int>();int constantAt=0;
        var transformColumns=TransformPaths.Select((path,index)=>(path,index)).ToDictionary(x=>x.path,x=>x.index);
        foreach(var encodedIndex in constantIndices)
        {
            int index=encodedIndex.GetInt32();Validation.Require(index>=0&&index<bindings.Length&&usedConstants.Add(index),"Invalid or duplicate native constant binding index");
            var binding=bindings[index];int type=binding.GetProperty("typeID").GetInt32();uint attribute=binding.GetProperty("attribute").GetUInt32();int width=type==4?(attribute==2?4:3):1;
            Validation.Require(constantAt<=constants.Length-width,"Native constant values are truncated");
            int column=type==4?transformColumns[binding.GetProperty("path").GetUInt32()]*10+(attribute==1?4:attribute==2?0:7):samples.Tracks*10+scalarColumns[attribute];
            for(int sample=0;sample<Samples;sample++)
            {
                float error=0,opposite=0;int at=Offset(sample)+column;
                for(int component=0;component<width;component++){error=Math.Max(error,Math.Abs(values[at+component]-constants[constantAt+component]));opposite=Math.Max(opposite,Math.Abs(values[at+component]+constants[constantAt+component]));}
                Validation.Require((type==4&&attribute==2?Math.Min(error,opposite):error)<=0.00001f,"Native constant metadata disagrees with decoded ACL values");
            }
            constantAt+=width;
        }
        Validation.Require(constantAt==constants.Length,"Trailing native animation constants");
        if(!HasHumanoid)
        {
            Validation.Require(PositionTracks[0]&&RotationTracks[0],"Generic root transform is not bound");
            for(int sample=0;sample<Samples;sample++)
            {
                for(uint axis=0;axis<3;axis++)Validation.Require(Math.Abs(ScalarValue(sample,axis)-ScalarValue(sample,7+axis))<=0.00001f,"Generic motion-relative translation is not yet supported");
                var motion=new Quaternion(ScalarValue(sample,3),ScalarValue(sample,4),ScalarValue(sample,5),ScalarValue(sample,6));var root=new Quaternion(ScalarValue(sample,10),ScalarValue(sample,11),ScalarValue(sample,12),ScalarValue(sample,13));NativeHumanoidRig.Rotation(motion);NativeHumanoidRig.Rotation(root);
                Validation.Require(1-Math.Abs(Quaternion.Dot(Quaternion.Normalize(motion),Quaternion.Normalize(root)))<=0.000001f,"Generic motion-relative rotation is not yet supported");
            }
        }
        if(rootSamples is not null)
        {
            Validation.Require(rootSamples.Tracks==samples.Tracks&&rootSamples.Scalars==rootTracks&&rootSamples.Samples==Samples&&rootSamples.SampleRate==SampleRate&&rootSamples.Values is not null&&rootSamples.Values.Length==(long)(samples.Tracks*10+rootTracks)*Samples,"Root ACL layout differs from primary animation");
            for(int sample=0;sample<Samples;sample++)
            {
                int start=sample*(samples.Tracks*10+rootTracks)+samples.Tracks*10;
                for(uint attribute=0;attribute<(HasHumanoid?28:14);attribute++)
                {
                    float error=Math.Abs(rootSamples.Values![start+(int)attribute]-ScalarValue(sample,attribute));
                    // Root and primary streams are independently quantized. Observed wrap seams
                    // differ by up to 8.85e-5; retain primary values and reject larger disagreement.
                    // 1e-4 is a consistency acceptance tolerance in encoded scalar units, not a
                    // format precision guarantee or a coordinate/pose correction.
                    Validation.Require(error<=0.0001f,"Native root buffer disagrees with primary scalar identity");
                    RootComparisonMaximumError=Math.Max(RootComparisonMaximumError,error);
                }
                if(!HasHumanoid)
                {
                    for(int axis=0;axis<3;axis++)Validation.Require(Math.Abs(rootSamples.Values![start+14+axis]-values[Offset(sample)+4+axis])<=0.00001f,"Generic root buffer disagrees with root translation track");
                    float error=0,opposite=0;for(int axis=0;axis<4;axis++){error=Math.Max(error,Math.Abs(rootSamples.Values![start+17+axis]-values[Offset(sample)+axis]));opposite=Math.Max(opposite,Math.Abs(rootSamples.Values![start+17+axis]+values[Offset(sample)+axis]));}Validation.Require(Math.Min(error,opposite)<=0.00001f,"Generic root buffer disagrees with root rotation track");
                }
            }
            RootScalarsValidated=true;
        }
    }

    /// <summary>
    /// Binds a classic single-stream clip decoded by <see cref="NativeStreamedClipDecoder"/>. The decode is checked
    /// against the clip's authored bindings, so no ACL header, mask or compressed byte is synthesized and no rest pose
    /// is substituted. A single stream has no independent root buffer, so the ACL root-agreement check is not
    /// applicable for this storage.
    /// </summary>
    public NativeAnimationBinding(JsonElement clip, NativeStreamedClipCurves curves)
    {
        Validation.Require(curves is not null && curves.Tracks is > 0 and <= 4096 && curves.Scalars is >= 14 and <= 4096
            && curves.Samples is > 0 and <= 1_000_000 && float.IsFinite(curves.SampleRate) && curves.SampleRate > 0, "Unsupported animation sample layout");
        long length = (curves!.Tracks * 10L + curves.Scalars) * curves.Samples;
        Validation.Require(length <= 16 * 1024 * 1024 && curves.Values is not null && curves.Values.Length == length && curves.Values.All(float.IsFinite), "Invalid animation sample values");
        var muscle = clip.GetProperty("m_MuscleClip");
        Validation.Require(NativeHumanoidRig.ArrayField(muscle.GetProperty("m_DeltaPose"), "m_DoFArray", 61).Length == 61, "Clip does not use the observed native61 muscle layout");
        // The classic streamed layout owns its own end rule (last key <= authored end, constant tail held by the
        // zero end coefficient), so its interval relation is left exactly as verified for that storage.
        Validation.Require(clip.GetProperty("m_SampleRate").GetSingle() == curves.SampleRate && muscle.GetProperty("m_StartTime").GetDouble() == 0
            && Math.Abs(muscle.GetProperty("m_StopTime").GetDouble() - (curves.Samples - 1) / (double)curves.SampleRate) <= 0.0001, "Unsupported native animation time interval");
        Duration = (curves.Samples - 1) / (double)curves.SampleRate;
        AuthoredEndBeyondGrid = false;
        var bindings = NativeHumanoidRig.ArrayField(clip.GetProperty("m_ClipBindingConstant"), "genericBindings", 16384);
        scalarColumns = [];
        var transform = new Dictionary<uint, (bool Position, bool Rotation, bool Scale)>();
        var seen = new HashSet<(uint Path, uint Attribute)>();
        foreach (var binding in bindings)
        {
            int type = binding.GetProperty("typeID").GetInt32(); uint attribute = binding.GetProperty("attribute").GetUInt32(); int custom = binding.GetProperty("customType").GetInt32();
            Validation.Require(binding.GetProperty("isPPtrCurve").GetInt32() == 0, "Unsupported object animation binding");
            if (type == 4)
            {
                Validation.Require(custom == 0 && attribute is 1 or 2 or 3, "Unsupported native transform curve binding");
                uint path = binding.GetProperty("path").GetUInt32();
                Validation.Require(seen.Add((path, attribute)), "Duplicate native transform curve binding");
                transform.TryGetValue(path, out var channels);
                transform[path] = (channels.Position || attribute == 1, channels.Rotation || attribute == 2, channels.Scale || attribute == 3);
                continue;
            }
            Validation.Require(type == 95 && binding.GetProperty("path").GetUInt32() == 0 && custom == (attribute < 143 ? 8 : 0), "Unsupported classic Animator scalar binding kind");
            // Muscle attributes identify the pose and must be unique. A repeated custom (>=143) attribute is only
            // collapsed by the classic decoder when its authored curves are equal on every sample; no curve wins.
            if (attribute < 143) Validation.Require(scalarColumns.TryAdd(attribute, scalarColumns.Count), "Duplicate native muscle attribute");
            else if (!scalarColumns.ContainsKey(attribute)) scalarColumns[attribute] = scalarColumns.Count;
        }
        HasHumanoid = scalarColumns.Keys.Any(x => x >= 14 && x < 143);
        Validation.Require(scalarColumns.Count == curves.Scalars && Enumerable.Range(0, HasHumanoid ? 143 : 14).All(x => scalarColumns.ContainsKey((uint)x)), "Required native scalar attributes are absent");
        Validation.Require(transform.Count == curves.Tracks && transform.Keys.All(curves.TransformPaths.Contains), "Decoded classic tracks disagree with the authored transform bindings");
        for (int track = 0; track < curves.Tracks; track++)
        {
            var channels = transform[curves.TransformPaths[track]];
            Validation.Require(channels.Position == curves.PositionTracks[track] && channels.Rotation == curves.RotationTracks[track] && channels.Scale == curves.ScaleTracks[track],
                "Decoded classic transform channels disagree with the authored bindings");
        }
        TransformPaths = Array.AsReadOnly(curves.TransformPaths);
        PositionTracks = Array.AsReadOnly(curves.PositionTracks); RotationTracks = Array.AsReadOnly(curves.RotationTracks); ScaleTracks = Array.AsReadOnly(curves.ScaleTracks);
        Samples = curves!.Samples; Scalars = curves.Scalars; SampleRate = curves.SampleRate; values = (float[])curves.Values!.Clone();
        CustomScalarAttributes = Array.AsReadOnly(curves.ScalarAttributes.Where(x => x >= 143).ToArray());
        StorageNotes = Array.AsReadOnly(curves.Notes);
        NonAclSingleStream = true;
    }

    public NativeHumanoidFrame HumanFrame(int sample)
    {
        Validation.Require(HasHumanoid,"Generic clip has no native body-muscle frame");
        int offset = Offset(sample) + TransformPaths.Count * 10;
        float Read(uint attribute) => values[offset + scalarColumns[attribute]];
        Vector3 V(uint first) => new(Read(first), Read(first + 1), Read(first + 2));
        Quaternion Q(uint first) => new(Read(first), Read(first + 1), Read(first + 2), Read(first + 3));
        return new(V(0), Q(3), V(7), Q(10), Enumerable.Range(42, 61).Select(x => Read((uint)x)).ToArray(), Enumerable.Range(103, 40).Select(x => Read((uint)x)).ToArray());
    }

    public float ScalarValue(int sample, uint attribute)
    {
        Validation.Require(scalarColumns.TryGetValue(attribute, out _), "Unknown native Animator scalar attribute");
        return values[Offset(sample) + TransformPaths.Count * 10 + scalarColumns[attribute]];
    }

    public Matrix4x4 TransformLocal(int sample, int track, Matrix4x4? importedRestLocal = null)
    {
        Validation.Require(track >= 0 && track < TransformPaths.Count, "Invalid native transform track"); int at = Offset(sample) + track * 10;
        Vector3 scale=Vector3.One,translation=Vector3.Zero;Quaternion q=Quaternion.Identity;
        Validation.Require(importedRestLocal.HasValue||PositionTracks[track]&&RotationTracks[track]&&ScaleTracks[track],"Unbound transform channels require the imported rest transform");
        if(importedRestLocal is {} rest)Validation.Require(Matrix4x4.Decompose(rest,out scale,out q,out translation),"Invalid imported transform rest basis");
        if(PositionTracks[track])translation=new(values[at+4],values[at+5],values[at+6]);
        if(RotationTracks[track])q=new(values[at],values[at+1],values[at+2],values[at+3]);
        if(ScaleTracks[track])scale=new(values[at+7],values[at+8],values[at+9]);
        NativeHumanoidRig.Rotation(q);return Matrix4x4.CreateScale(scale)*Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q))*Matrix4x4.CreateTranslation(translation);
    }
    /// <summary>Exact decoded native generic channels of one track and sample before any imported rest basis is
    /// applied. Used to preserve a track verbatim when its target identity does not exist in the imported rig.</summary>
    public (Vector3 Translation,Quaternion Rotation,Vector3 Scale) RawTransform(int sample,int track)
    {
        Validation.Require(track>=0&&track<TransformPaths.Count,"Invalid native transform track");int at=Offset(sample)+track*10;
        Vector3 translation=PositionTracks[track]?new(values[at+4],values[at+5],values[at+6]):Vector3.Zero;
        Vector3 scale=ScaleTracks[track]?new(values[at+7],values[at+8],values[at+9]):Vector3.One;
        Quaternion rotation=RotationTracks[track]?new(values[at],values[at+1],values[at+2],values[at+3]):Quaternion.Identity;
        if(RotationTracks[track])NativeHumanoidRig.Rotation(rotation);
        return (translation,rotation,scale);
    }
    private int Offset(int sample) { Validation.Require(sample >= 0 && sample < Samples, "Animation sample index is out of range"); return sample * (TransformPaths.Count * 10 + Scalars); }
}

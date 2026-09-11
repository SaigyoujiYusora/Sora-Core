using System.Buffers.Binary;
using System.Text;

namespace Sora.Core;

public sealed record NativeAnimationMaskReference(string Layer, string Hash, string? Path);
public sealed record NativeAnimationConfigHeader(string Path, ResourceFileRecord Source, int ControllerOffset,
    string ControllerHash, string ControllerPath, ResolvedAsset Controller, NativeAnimationMaskReference[] Masks);

/// <summary>Bounded observed 0FFF native config prefix: named mask references followed by the controller hash.</summary>
public static class NativeAnimationConfig
{
    public static NativeAnimationConfigHeader ReadController(GameResources game,string path)
    {
        byte[] bytes=game.GetBytes(path);
        Validation.Require(bytes.Length>=30&&bytes[0]==0x0f&&bytes[1]==0xff&&bytes.AsSpan(2,16).IndexOfAnyExcept((byte)0)<0,"Unsupported native animation config prefix");
        int at=18;
        int I32(){Validation.Require(at<=bytes.Length-4,"Truncated native animation config");int value=BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));at+=4;return value;}
        long Hash(){Validation.Require(at<=bytes.Length-8,"Truncated native animation resource hash");long value=BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at));at+=8;return value;}
        int count=I32();Validation.Require(count>=0&&count<=64,"Native animation mask count exceeds supported prefix");
        var masks=new List<NativeAnimationMaskReference>();
        for(int i=0;i<count;i++) {
            Validation.Require(at<bytes.Length&&bytes[at++]==2,"Unsupported native layer-name encoding");int length=I32();Validation.Require(length>0&&length<=4096&&at<=bytes.Length-length,"Invalid native layer name length");
            string name=new UTF8Encoding(false,true).GetString(bytes,at,length);at+=length;long hash=Hash();
            var matches=game.Manifest.Assets.Where(asset=>asset.Hash==hash).DistinctBy(asset=>(asset.Path,asset.Bundle)).ToArray();
            masks.Add(new(name,hash.ToString("x16"),matches.Length==1?matches[0].Path:null));
        }
        int offset=at;long controllerHash=Hash();var controllers=game.Manifest.Assets.Where(asset=>asset.Hash==controllerHash).DistinctBy(asset=>(asset.Path,asset.Bundle)).ToArray();
        Validation.Require(controllers.Length==1,"Native config controller hash is absent or ambiguous");
        var address=controllers[0];int type=address.Path.EndsWith(".overridecontroller",StringComparison.OrdinalIgnoreCase)?221:address.Path.EndsWith(".controller",StringComparison.OrdinalIgnoreCase)?91:0;
        Validation.Require(type!=0,"Native config hash is not an Animator controller resource");
        var controller=game.ResolveAddress(address,type);
        return new(path,game.LogicalSource(path),offset,controllerHash.ToString("x16"),address.Path,controller,masks.ToArray());
    }
}

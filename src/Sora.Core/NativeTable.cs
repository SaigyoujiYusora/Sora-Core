using System.Buffers.Binary;
using System.Text;

namespace Sora.Core;

/// <summary>Independent bounded reader for native self-described TableCfg tables.</summary>
public sealed class NativeTable
{
    private sealed record Type(byte Code, uint Reference = 0, Type? Element = null, Type? Value = null);
    private sealed record Definition(string Name, (string Name, Type Type)[] Fields);
    private readonly byte[] bytes;
    private readonly Dictionary<uint,Definition> definitions = [];
    private readonly Type keyType;
    private readonly Type valueType;
    private readonly int dataStart;
    private readonly int stringStart;
    private sealed class ReadBudget { public int Values; }
    public string Name { get; }
    public IReadOnlyDictionary<string,byte> RowFieldTypes => definitions.TryGetValue(valueType.Reference,out var row) ? row.Fields.ToDictionary(f=>f.Name,f=>f.Type.Code,StringComparer.Ordinal) : new Dictionary<string,byte>();

    public NativeTable(byte[] bytes)
    {
        Validation.Require(bytes.Length is >= 16 and <= DatabaseFile.MaxPayloadBytes,"Invalid native table length");
        this.bytes=bytes;int header=0;int schema=I32(ref header),root=I32(ref header);dataStart=I32(ref header);
        Validation.Require(schema==12&&schema<=root&&root<dataStart&&dataStart<bytes.Length,"Invalid native table section offsets");
        int at=schema,count=Count(ref at,4096);
        for(int i=0;i<count;i++) {
            var type=Descriptor(ref at,0);Validation.Require(type.Code is 6 or 8,"Unsupported native table definition");
            string name=Text(ref at,root);int fields=Count(ref at,4096);var parsed=new (string,Type)[fields];
            for(int j=0;j<fields;j++) {
                string field=Text(ref at,root);
                parsed[j]=(field,type.Code==6 ? new Type(6,unchecked((uint)I32(ref at))) : Descriptor(ref at,0));
            }
            Validation.Require(definitions.TryAdd(type.Reference,new(name,parsed)),"Duplicate native table type");
        }
        Validation.Require(at==root,"Native table schema section length mismatch");
        Validation.Require(U8(ref at)==10,"Native table root must be a dictionary");Name=Text(ref at,dataStart);
        keyType=Descriptor(ref at,0);valueType=Descriptor(ref at,0);
        count=Count(ref at,1_000_000);stringStart=at;
        for(int i=0;i<count;i++) { if(i%512==0)OperationProgress.Report("read-native-table-strings",i,count,Name); _=Text(ref at,dataStart); }
        Validation.Require(at<=dataStart&&dataStart-at is >=128 and <=135 && bytes.AsSpan(at,dataStart-at).IndexOfAnyExcept((byte)0)<0,"Native table string section length mismatch");
    }

    /// <summary>Only selected row fields are expanded; skipped compound fields do not allocate their children.</summary>
    public IEnumerable<(object Key, object? Value, int Offset)> Rows(params string[] fields)
    {
        int at=dataStart;int capacity=Count(ref at,1_000_000);Range(at,checked(capacity*8));
        int total=0,processed=0;var keys=new HashSet<object>();var budget=new ReadBudget();var projection=fields.Length==0?null:fields.ToHashSet(StringComparer.Ordinal);
        for(int bucket=0;bucket<capacity;bucket++) {
            if(bucket%512==0) OperationProgress.Report("read-native-table",bucket,capacity,Name);
            int cursor=at+bucket*8;int offset=I32(ref cursor),count=Count(ref cursor,1_000_000);
            Validation.Require(count==0||offset>=at+capacity*8&&offset<bytes.Length,"Invalid native table bucket offset");
            total=checked(total+count);Validation.Require(total<=1_000_000,"Native table row limit exceeded");
            cursor=offset;
            for(int row=0;row<count;row++) {
                if(processed++%256==0)OperationProgress.Report("read-native-table-rows",processed-1,detail:Name);
                int rowOffset=cursor;var key=Read(keyType,ref cursor,0,null,budget)??throw new InvalidDataException("Null native table key");
                Validation.Require(keys.Add(key),"Duplicate native table key");
                yield return (key,Read(valueType,ref cursor,0,projection,budget),rowOffset);
            }
        }
    }

    private object? Read(Type type,ref int at,int depth,HashSet<string>? projection,ReadBudget budget)
    {
        Validation.Require(depth<=16,"Native table recursion limit exceeded");
        Validation.Require(++budget.Values<=2_000_000,"Native table expanded value budget exceeded");
        if(budget.Values%2048==0)OperationProgress.Report("decode-native-table-values",budget.Values,detail:Name);
        switch(type.Code) {
            case 0: {byte flag=U8(ref at);Validation.Require(flag<=1,"Invalid native boolean");return flag!=0;}
            case 2: return I32(ref at);
            case 3: Align(ref at,8);Range(at,8);ulong integer=BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at));at+=8;return integer;
            case 4: return BitConverter.Int32BitsToSingle(I32(ref at));
            case 5: Align(ref at,8);Range(at,8);double number=BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at)));at+=8;return number;
            case 6: return I32(ref at);
            case 7: {int text=I32(ref at);if(text==-1)return null;Validation.Require(text>=stringStart&&text<dataStart,"Native string pointer is outside string section");return Text(ref text,dataStart);}
            case 8: {
                int pointer=I32(ref at);if(pointer==-1)return null;Validation.Require(pointer>=dataStart&&pointer<bytes.Length,"Native object pointer is outside payload");
                Validation.Require(definitions.TryGetValue(type.Reference,out var definition),"Unknown native table object type");
                var output=new Dictionary<string,object?>(StringComparer.Ordinal);
                foreach(var field in definition!.Fields) {
                    if(projection is null||projection.Contains(field.Name)) output.Add(field.Name,Read(field.Type,ref pointer,depth+1,null,budget));
                    else Skip(field.Type,ref pointer);
                }
                return output;
            }
            case 9: {
                int pointer=I32(ref at);if(pointer==-1)return null;Validation.Require(pointer>=dataStart&&pointer<bytes.Length,"Native array pointer is outside payload");
                int count=Count(ref pointer,100_000);var output=new object?[count];
                for(int i=0;i<count;i++)output[i]=Read(type.Element!,ref pointer,depth+1,null,budget);return output;
            }
            default: throw new InvalidDataException("Unsupported native table value code: "+type.Code);
        }
    }
    private void Skip(Type type,ref int at) {
        int width=type.Code switch {0=>1,2 or 4 or 6 or 7 or 8 or 9 or 10=>4,3 or 5=>8,_=>throw new InvalidDataException("Unsupported native table field code: "+type.Code)};
        Align(ref at,width);Range(at,width);at+=width;
    }
    private Type Descriptor(ref int at,int depth) {
        Validation.Require(depth<=16,"Native type recursion limit exceeded");byte code=U8(ref at);
        return code switch {6 or 8=>new(code,unchecked((uint)I32(ref at))),9=>new(code,Element:Descriptor(ref at,depth+1)),10=>new(code,Element:Descriptor(ref at,depth+1),Value:Descriptor(ref at,depth+1)),_=>new(code)};
    }
    private void Range(int at,int length)=>Validation.Require(at>=0&&length>=0&&at<=bytes.Length-length,"Native table read exceeds bounds");
    private static void Align(ref int at,int alignment)=>at=checked((at+alignment-1)&~(alignment-1));
    private byte U8(ref int at){Range(at,1);return bytes[at++];}
    private int I32(ref int at){Align(ref at,4);Range(at,4);int result=BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));at+=4;return result;}
    private int Count(ref int at,int maximum){int count=I32(ref at);Validation.Require(count>=0&&count<=maximum,"Native table count exceeds limit");return count;}
    private string Text(ref int at,int end){Validation.Require(at>=0&&at<end&&end<=bytes.Length,"Invalid native text bounds");int finish=Array.IndexOf(bytes,(byte)0,at,end-at);Validation.Require(finish>=at&&finish-at<=4*1024*1024,"Invalid native text length");string result=new UTF8Encoding(false,true).GetString(bytes,at,finish-at);at=finish+1;return result;}
}

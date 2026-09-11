using System.Text;
using Sora.Core;

internal static class NativeTableTests
{
    public static void Run(Action<string,Action> test,Action<Action> reject)
    {
        byte[] Fixture(bool duplicate=false,int count=1) {
            using var stream=new MemoryStream();using var writer=new BinaryWriter(stream,Encoding.UTF8,true);
            void Align(int width){while(stream.Position%width!=0)writer.Write((byte)0);}
            void Text(string text){writer.Write(Encoding.UTF8.GetBytes(text));writer.Write((byte)0);}
            writer.Write(12);writer.Write(16);writer.Write(0);writer.Write(0);
            writer.Write((byte)10);Text("I18nTextTable_CN");writer.Write((byte)3);writer.Write((byte)7);Align(4);writer.Write(1);
            int text=(int)stream.Position;Text("原生名称");Align(4);writer.Write(new byte[128]);int root=(int)stream.Position;
            writer.Write(1);writer.Write(root+12);writer.Write(duplicate?2:count);
            for(int i=0;i<(duplicate?2:count);i++){Align(8);writer.Write(0xabcdef1234567890UL+(duplicate?0UL:(ulong)i));writer.Write(text);}
            stream.Position=8;writer.Write(root);return stream.ToArray();
        }
        test("native table aligned UInt64 dictionary retains Chinese text",()=>{
            var table=new NativeTable(Fixture());var row=table.Rows().Single();
            if(table.Name!="I18nTextTable_CN"||(ulong)row.Key!=0xabcdef1234567890UL||(string?)row.Value!="原生名称")throw new Exception();
        });
        test("native table cancellation is observed inside a single large bucket",()=>{
            var table=new NativeTable(Fixture(count:600));bool cancelled=false;
            OperationProgress.Sink=update=>{if(update.Stage=="read-native-table-rows"&&update.Completed>=256)throw new OperationCanceledException();};
            try{_ = table.Rows().ToArray();}catch(OperationCanceledException){cancelled=true;}finally{OperationProgress.Sink=null;}
            if(!cancelled)throw new Exception();
        });
        test("native table rejects truncation duplicate keys and bad string offset",()=>{
            var bytes=Fixture();reject(()=>new NativeTable(bytes[..10]));reject(()=>new NativeTable(Fixture(true)).Rows().ToArray());
            bytes[^1]=0x7f;reject(()=>new NativeTable(bytes).Rows().ToArray());
        });
    }
}

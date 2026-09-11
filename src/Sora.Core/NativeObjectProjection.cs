using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sora.Core;

/// <summary>Projects only consumed native fields; unrelated controller sentinels need not enter JSON transport.</summary>
public static class NativeObjectProjection
{
    public static JsonElement Json(SerializedObject value,params string[] fields)
    {
        var selected=new Dictionary<string,object?>(StringComparer.Ordinal);
        if(value.Data is IReadOnlyDictionary<string,object?> dictionary) {
            foreach(string field in fields)if(dictionary.TryGetValue(field,out var item))selected.Add(field,item);
        } else if(value.Data is JsonObject node) {
            foreach(string field in fields)if(node.TryGetPropertyValue(field,out var item))selected.Add(field,item);
        } else if(value.Data is JsonElement element) {
            foreach(string field in fields)if(element.TryGetProperty(field,out var item))selected.Add(field,item);
        } else throw new InvalidDataException("Unsupported native object projection representation");
        return JsonSerializer.SerializeToElement(selected,WireJson.Options);
    }
}

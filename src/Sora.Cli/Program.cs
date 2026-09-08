using System.Text;
using System.Text.Json;
using Sora.Core;

Console.InputEncoding = new UTF8Encoding(false, true);
Console.OutputEncoding = new UTF8Encoding(false);
try
{
    if (args.Length == 1 && args[0] == "acl-worker")
    {
        AclCodec.RunWorker(Console.OpenStandardInput(), Console.Out);
    }
    else if (args.Length == 1 && args[0] == "rpc")
    {
        using var input = new BufferedStream(Console.OpenStandardInput(), 16384);
        while (true)
        {
            using var buffer = new MemoryStream(); bool oversized = false; int character;
            while ((character = input.ReadByte()) != -1 && character != '\n')
                if (buffer.Length < 1024 * 1024) buffer.WriteByte((byte)character); else oversized = true;
            if (character == -1 && buffer.Length == 0) break;
            string response;
            try { response = oversized ? Failure(null, "invalid_input", "Request exceeds limit") : Handle(new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length)); }
            catch (DecoderFallbackException) { response = Failure(null, "invalid_input", "Request is not valid UTF-8"); }
            Console.WriteLine(response);
        }
    }
    else if (args.Length == 3 && args[0] == "pack")
    {
        using var input = File.OpenRead(args[1]);
        Validation.Require(input.Length <= DatabaseFile.MaxPayloadBytes, "Input exceeds limit");
        var payload = new byte[(int)input.Length]; input.ReadExactly(payload);
        DatabaseFile.WriteAtomic(args[2], DatabaseFile.ParsePayload(payload));
        Console.WriteLine("{\"ok\":true}");
    }
    else if (args.Length == 4 && args[0] == "import-character")
    {
        var resources = new GameResources(args[1]);
        var database = NativeCharacterImport.Import(resources, args[2]) with { ResourceIndex = resources.SnapshotResourceIndex() };
        DatabaseFile.WriteAtomic(args[3], database);
        var scene = database.Assets[0].Scene!;
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, meshes = scene.Meshes.Length, bones = scene.Bones.Length, materials = scene.Materials.Length, textures = scene.Textures?.Length ?? 0 }, WireJson.Options));
    }
    else if (args.Length == 4 && args[0] == "character-geometry")
    {
        using var input = File.OpenRead(args[1]);
        Validation.Require(input.Length <= DatabaseFile.MaxPayloadBytes, "Character input exceeds limit");
        byte[] bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        var database = CharacterGeometry.Convert(SerializedAssets.Decode(bytes), args[2]);
        DatabaseFile.WriteAtomic(args[3], database);
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, meshes = database.Assets[0].Scene!.Meshes.Length, bones = database.Assets[0].Scene!.Bones.Length }, WireJson.Options));
    }
    else if (args.Length == 2 && args[0] == "blc-list")
    {
        Console.WriteLine(JsonSerializer.Serialize(BlockIndex.Read(args[1]), WireJson.Options));
    }
    else if (args.Length == 4 && args[0] == "blc-extract")
    {
        var index = BlockIndex.Read(args[1]);
        var matches = index.Resources.Where(x => x.Name.EndsWith(args[2], StringComparison.Ordinal)).ToArray();
        Validation.Require(matches.Length == 1, "Select an unambiguous BLC resource name");
        byte[] bytes = BlockIndex.Extract(args[1], matches[0]);
        using var output = new FileStream(args[3], FileMode.CreateNew, FileAccess.Write, FileShare.None); output.Write(bytes);
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, bytes = bytes.Length }, WireJson.Options));
    }
    else if (args.Length == 2 && args[0] == "unity-inspect")
    {
        using var input = File.OpenRead(args[1]);
        Validation.Require(input.Length <= DatabaseFile.MaxPayloadBytes, "Serialized asset exceeds limit");
        byte[] bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        Console.WriteLine(JsonSerializer.Serialize(SerializedAssets.Decode(bytes), WireJson.Options));
    }
    else if (args.Length == 2 && args[0] == "vfs-list")
    {
        using var archive = new VfsArchive(args[1]);
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, entries = archive.Entries }, WireJson.Options));
    }
    else if (args.Length == 4 && args[0] == "vfs-extract")
    {
        using var archive = new VfsArchive(args[1]);
        byte[] bytes = archive.Extract(args[2]);
        using var output = new FileStream(args[3], FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes);
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, bytes = bytes.Length }, WireJson.Options));
    }
    else if (args.Length == 3 && args[0] == "manifest")
    {
        using var input = File.OpenRead(args[1]);
        var manifest = NativeManifest.Read(input);
        DatabaseFile.WriteAtomic(args[2], manifest.ToDatabase());
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, manifest.Version, bundles = manifest.Bundles.Length, assets = manifest.Assets.Length }, WireJson.Options));
    }
    else
    {
        Console.Error.WriteLine("Usage:\n  Sora-Core rpc\n  Sora-Core pack <document.json> <output.sredb>\n  Sora-Core manifest <manifest.hgmmap> <output.sredb>\n  Sora-Core blc-list <index.blc>\n  Sora-Core blc-extract <index.blc> <resource-name> <output>\n  Sora-Core vfs-list <bundle>\n  Sora-Core vfs-extract <bundle> <entry-name> <output>\n  Sora-Core unity-inspect <serialized-asset>\n  Sora-Core character-geometry <serialized-asset> <identity> <output.sredb>\n  Sora-Core import-character <game-root> <character-query> <output.sredb>");
        Environment.ExitCode = 2;
    }
}
catch (Exception exception)
{ Console.Error.WriteLine(exception.Message); Environment.ExitCode = 1; }

static string Failure(string? id, string code, string message)
    => JsonSerializer.Serialize(new { protocol = 1, id, ok = false, error = new { code, message } }, WireJson.Options);

static string Handle(string line)
{
    string? id = null;
    try
    {
        Validation.Require(Encoding.UTF8.GetByteCount(line) <= 1024 * 1024, "Request exceeds limit");
        using var request = WireJson.Parse(Encoding.UTF8.GetBytes(line));
        var root = request.RootElement;
        Validation.Require(root.ValueKind == JsonValueKind.Object, "Request must be an object");
        id = root.GetProperty("id").GetString();
        Validation.Require(id is not null && id.Length <= 128, "Invalid request ID");
        Validation.Require(root.GetProperty("protocol").GetInt32() == 1, "Unsupported protocol version");
        var method = root.GetProperty("method").GetString();
        var parameters = root.GetProperty("params");
        Validation.Require(parameters.ValueKind == JsonValueKind.Object, "Params must be an object");
        object result;
        if (method == "capabilities") result = new { product = "Sora-Core", version = "0.1.0", databaseVersions = new[] { 1, 2 }, methods = new[] { "capabilities", "inspect", "search", "closure", "scene" }, nativeGameExtraction = true, nativeExtraction = new { entryPoint = "CLI import-character", geometry = true, materials = "native descriptors and textures", humanoidAnimation = false, authoredFaceControls = true, maps = false, verifiedCharacters = new[] { "azrila" } } };
        else
        {
            Validation.Require(method is "inspect" or "search" or "closure" or "scene", "Unsupported method");
            var database = DatabaseFile.Read(parameters.GetProperty("path").GetString() ?? throw new InvalidDataException("Missing input path"));
            var catalog = new Catalog(database);
            result = method switch
            {
                "inspect" => new { database.GameVersion, assets = database.Assets.Length },
                "search" => catalog.Search(parameters.TryGetProperty("query", out var query) ? query.GetString() ?? "" : "", parameters.TryGetProperty("offset", out var offset) ? offset.GetInt32() : 0, parameters.TryGetProperty("limit", out var limit) ? limit.GetInt32() : 100),
                "closure" => catalog.Closure(parameters.GetProperty("asset").GetString()!),
                "scene" => catalog.Get(parameters.GetProperty("asset").GetString()!).Scene ?? throw new InvalidDataException("Asset has no decoded scene"),
                _ => throw new InvalidDataException("Unsupported method")
            };
        }
        return JsonSerializer.Serialize(new { protocol = 1, id, ok = true, result }, WireJson.Options);
    }
    catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException or OverflowException)
    { return Failure(id, exception is KeyNotFoundException ? "not_found" : "invalid_input", exception.Message); }
    catch (Exception)
    { return Failure(id, "internal_error", "The request could not be processed"); }
}

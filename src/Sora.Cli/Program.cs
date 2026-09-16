using System.Text;
using System.Text.Json;
using Sora.Core;


internal static class Program
{
    public static int Main(string[] args)
    {
        ProcessErrorMode.Configure();
        try { return RunAsync(args).GetAwaiter().GetResult(); }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<int> RunAsync(string[] args)
    {
Console.InputEncoding = new UTF8Encoding(false, true);
Console.OutputEncoding = new UTF8Encoding(false);
try
{
    if (args.Length == 1 && args[0] == "acl-worker")
    {
        AclCodec.RunWorker(Console.OpenStandardInput(), Console.Out);
    }
    else if (args.Length == 1 && args[0] is "rpc-task" or "rpc-task-session")
    {
        SessionDatabase.Enabled = args[0] == "rpc-task-session";
        await TaskTransport.Run(Handle, SessionDatabase.Enabled);
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
    else if (args.Length == 3 && args[0] == "npc-search")
    {
        Console.WriteLine(JsonSerializer.Serialize(NativeNpcImport.Search(new GameResources(args[1]),args[2]),WireJson.Options));
    }
    else if (args.Length == 4 && args[0] == "import-npc")
    {
        var resources=new GameResources(args[1]);
        var database=NativeNpcImport.Import(resources,args[2]) with {ResourceIndex=resources.SnapshotResourceIndex()};
        DatabaseFile.WriteAtomic(args[3],database);
        var scene=database.Assets[0].Scene!;
        Console.WriteLine(JsonSerializer.Serialize(new {ok=true,meshes=scene.Meshes.Length,bones=scene.Bones.Length,controls=scene.FaceDriver?.Controls.Length},WireJson.Options));
    }
    else if (args.Length == 4 && args[0] == "import-character")
    {
        var resources = new GameResources(args[1]);
        var database = NativeCharacterImport.Import(resources, args[2]) with { ResourceIndex = resources.SnapshotResourceIndex() };
        DatabaseFile.WriteAtomic(args[3], database);
        var scene = database.Assets[0].Scene!;
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, meshes = scene.Meshes.Length, bones = scene.Bones.Length, materials = scene.Materials.Length, textures = scene.Textures?.Length ?? 0 }, WireJson.Options));
    }
    else if (args.Length is 3 or 4 && args[0] == "animation-search")
    {
        var resources = new GameResources(args[1]);
        Console.WriteLine(JsonSerializer.Serialize(NativeAnimationService.Search(resources, args[2], args.Length == 4 ? args[3] : null), WireJson.Options));
    }
    else if (args.Length is 3 or 4 && args[0] == "animation-clips")
    {
        var resources = new GameResources(args[1]);
        Console.WriteLine(JsonSerializer.Serialize(NativeAnimationService.Discover(resources, args[2], args.Length == 4 ? args[3] : null), WireJson.Options));
    }
    else if (args.Length is 6 or 8 && args[0] == "animation-export")
    {
        var resources = new GameResources(args[1]);
        var database = DatabaseFile.Read(args[2]);
        var converted = NativeAnimationService.Import(resources, database, args[3], args[4],
            selection: args.Length == 8 ? new NativeAnimationSelection(args[6], args[7]) : null);
        var asset = Catalog.For(database).Get(args[3]);
        var scene = asset.Scene!;
        Validation.Require(!scene.Clips.Any(clip => clip.Name == converted.Clip.Name), "A clip with this name already exists in the database");
        var updated = asset with { Scene = scene with { Clips = [.. scene.Clips, converted.Clip] },
            Detail = asset.Detail.Replace("body animation not included", "native animation included", StringComparison.Ordinal) };
        DatabaseFile.WriteAtomic(args[5], database with {
            Assets = database.Assets.Select(item => item.Id == asset.Id ? updated : item).ToArray(),
            ResourceIndex = EndfieldResourceIndexMerge.Merge(database.ResourceIndex, resources.SnapshotResourceIndex()) });
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, converted.Clip.Name, converted.Clip.Duration, converted.Clip.Fps,
            tracks = converted.Clip.Tracks.Length }, WireJson.Options));
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
        Console.Error.WriteLine("Usage:\n  Sora-Core rpc\n  Sora-Core pack <document.json> <output.sredb>\n  Sora-Core manifest <manifest.hgmmap> <output.sredb>\n  Sora-Core blc-list <index.blc>\n  Sora-Core blc-extract <index.blc> <resource-name> <output>\n  Sora-Core vfs-list <bundle>\n  Sora-Core vfs-extract <bundle> <entry-name> <output>\n  Sora-Core unity-inspect <serialized-asset>\n  Sora-Core character-geometry <serialized-asset> <identity> <output.sredb>\n  Sora-Core import-character <game-root> <character-query> <output.sredb>\n  Sora-Core npc-search <game-root> <query>\n  Sora-Core import-npc <game-root> <npc-query> <output.sredb>\n  Sora-Core animation-search <game-root> <query> [<character-prefab>]\n  Sora-Core animation-clips <game-root> <resource> [<character-prefab>]\n  Sora-Core animation-export <game-root> <input.sredb> <asset> <resource> <output.sredb> [<cab> <path-id>]");
        Environment.ExitCode = 2;
    }
}
catch (Exception exception)
{ Console.Error.WriteLine(exception.Message); Environment.ExitCode = 1; }


        return Environment.ExitCode;
    }

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
        if (method == "capabilities") result = new { product = "Sora-Core", version = "0.2.1", assemblyInformationalVersion = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(Program).Assembly)?.InformationalVersion, assemblyVersion = typeof(Program).Assembly.GetName().Version?.ToString(), build = new { sourceKind = "working-tree-candidate", sourceSnapshotFile = "source-snapshot.json" }, taskTransport = "rpc-task / rpc-task-session", cancellation = "session cancel requires targetId; one active request, extra requests return busy", databaseVersions = new[] { 1, 2, 3 }, methods = new[] { "game-validate", "database-build", "character-equipment", "equipment-assembly", "compatible-weapons", "weapon-assembly", "pose-map", "capabilities", "map-status", "map-read", "inspect", "search", "closure", "scene", "animation-search-page", "animation-search", "animation-clips", "animation-import", "equipment-animation-plan", "equipment-animation-bake", "equipment-skill-window-bake", "skill-search" }, nativeGameExtraction = true, nativeNpcExtraction = new { entryPoint = "CLI npc-search/import-npc", verifiedSelections = new[] { "npc_girl_efengineer_a_01" } }, equipmentAnimation = new { proofContract = NativeEquipmentAnimationService.ProofContract, transport = "animation-clips / animation-import with equipment selector", rig = "native-equipment-source-path", sampling = "non-ACL scalar blocks or ACL transform tracks on the authored native frame grid", runtime = "single controller layer; trigger and exit-time transitions with local TRS crossfade; no visibility or damping" }, nativeExtraction = new { entryPoint = "CLI import-character; RPC animation-search/animation-clips/animation-import", geometry = true, materials = "native descriptors and textures", humanoidAnimation = true, animationContract = "Endfield native61, ACL wire version 10", authoredFaceControls = true, maps = false, verifiedCharacters = new[] { "azrila", "pelica", "wolfgd" } } };
        else if (method == "game-validate" || method == "database-build")
        {
            var game = method == "game-validate" ? SessionGameResources.Read(parameters.GetProperty("root").GetString()!) : new GameResources(parameters.GetProperty("root").GetString()!);
            if (method == "database-build") {
                var database = GameCatalog.Build(game);
                OperationProgress.Report("write-database");
                var storage = DatabaseFile.WriteAtomicValidated(parameters.GetProperty("path").GetString()!, database);
                result = new { database.GameVersion, assets = database.Assets.Length, database.CatalogSource, storage.FormatVersion, storage.PayloadBytes, storage.MaxPayloadBytes };
            } else {
                var stored = parameters.TryGetProperty("path", out var path) ? SessionDatabase.ReadValidated(path.GetString()!) : null;
                bool matches = stored is null || GameCatalog.Matches(stored.Database, game);
                result = new { game.Manifest.Version, manifestHash = game.Manifest.Hash, manifestRevision = game.Manifest.Revision, sourceSelection = game.SourceSelectionPolicy, cacheScope = "index bytes and physical range metadata; payload content checked on extraction",
                    matches, databaseCompatibility = stored is null ? null : DatabaseCompatibilityPolicy.Inspect(stored.Database, game.Manifest.Version, matches), formatVersion = stored?.Storage.FormatVersion, payloadBytes = stored?.Storage.PayloadBytes, maxPayloadBytes = stored?.Storage.MaxPayloadBytes };
            }
        }
        else if (method is "compatible-weapons" or "weapon-assembly")
        {
            var database=SessionDatabase.Read(parameters.GetProperty("path").GetString()!);string gameRoot=parameters.GetProperty("root").GetString()!;
            var game=new GameResources(gameRoot);Validation.Require(database.CatalogSource is null||GameCatalog.Matches(database,game),"Game metadata changed; update the catalog");
            string asset=parameters.GetProperty("asset").GetString()!;
            result=method=="compatible-weapons" ? NativeWeaponAssembly.Compatible(game,database,asset,parameters.TryGetProperty("query",out var query)?query.GetString()??"":"",parameters.TryGetProperty("offset",out var offset)?offset.GetInt32():0,parameters.TryGetProperty("limit",out var limit)?limit.GetInt32():100)
                : NativeWeaponAssembly.Resolve(game,database,asset,parameters.GetProperty("weapon").GetString()!,gameRoot);
        }
        else if (method == "equipment-assembly")
        {
            var database=SessionDatabase.Read(parameters.GetProperty("path").GetString()!);
            var asset=Catalog.For(database).Get(parameters.GetProperty("asset").GetString()!);
            string gameRoot=parameters.GetProperty("root").GetString()!;
            var game=new GameResources(gameRoot);
            Validation.Require(database.CatalogSource is null||GameCatalog.Matches(database,game),"Game metadata changed; rebuild the catalog");
            var scene=asset.Scene??GameCatalog.Scene(database,asset.Id,gameRoot);
            var assembly=NativeEquipmentAssembly.Resolve(game,scene,asset.Locator?.Path??asset.Id);
            result=parameters.TryGetProperty("includeOwner",out var includeOwner)&&includeOwner.GetBoolean() ? new {scene,equipment=assembly} : (object)assembly;
        }
        else if (method == "pose-map")
        {
            var database=SessionDatabase.Read(parameters.GetProperty("path").GetString()!);
            var asset=Catalog.For(database).Get(parameters.GetProperty("asset").GetString()!);
            string gameRoot=parameters.GetProperty("root").GetString()!;
            var game=new GameResources(gameRoot);
            Validation.Require(database.CatalogSource is null || GameCatalog.Matches(database,game),"Game metadata changed; rebuild the catalog");
            Validation.Require(database.ResourceIndex is null || database.ResourceIndex.ManifestHash==game.Manifest.Hash,"Game resources changed since scene import");
            var scene=asset.Scene??GameCatalog.Scene(database,asset.Id,gameRoot);
            OperationProgress.Report("map-native-pose");
            result=NativePoseService.Map(game,scene,asset.Locator?.Path??asset.Id);
        }
        else if (method == "character-equipment")
        {
            var database=SessionDatabase.Read(parameters.GetProperty("path").GetString()!);
            var asset=Catalog.For(database).Get(parameters.GetProperty("asset").GetString()!);
            var game=new GameResources(parameters.GetProperty("root").GetString()!);
            Validation.Require(database.CatalogSource is null || GameCatalog.Matches(database,game),"Game metadata changed; rebuild the catalog");
            string identity=asset.Metadata?.RowId ?? Path.GetFileNameWithoutExtension(asset.Locator?.Path ?? asset.Id).Replace("_uimodel", "",StringComparison.Ordinal);
            result=NativeCharacterEquipment.Read(game,identity);
        }
        else if (method == "map-status") result = new PlaceholderMapDataReader().Status;
        else if (method == "map-read")
        {
            var mapRequest = parameters.Deserialize<MapDataRequest>(WireJson.Options)
                ?? throw new InvalidDataException("Missing map request");
            result = new PlaceholderMapDataReader().Read(mapRequest);
        }
        else if (method == "animation-search-page")
        {
            string animationRoot=parameters.GetProperty("root").GetString()!;string? character=null;
            if(parameters.TryGetProperty("asset",out var selected)) {
                var database=SessionDatabase.Read(parameters.GetProperty("path").GetString()!);var asset=Catalog.For(database).Get(selected.GetString()!);
                var game=SessionGameResources.Read(animationRoot);Validation.Require(database.CatalogSource is null||GameCatalog.Matches(database,game),"Game metadata changed; update the catalog");
                character=asset.Locator?.Path??asset.Id;
            }
            result=NativeAnimationCatalog.Page(SessionAnimationCatalog.Read(animationRoot,character),parameters.TryGetProperty("query",out var query)?query.GetString()??"":"",parameters.TryGetProperty("offset",out var offset)?offset.GetInt32():0,parameters.TryGetProperty("limit",out var limit)?limit.GetInt32():100,parameters.TryGetProperty("category",out var category)?category.GetString():null);
        }
        else if (method == "animation-search")
        {
            var resources = new GameResources(parameters.GetProperty("root").GetString() ?? throw new InvalidDataException("Missing game folder"));
            string? character = null;
            if (parameters.TryGetProperty("asset", out var selected))
            {
                var database = SessionDatabase.Read(parameters.GetProperty("path").GetString() ?? throw new InvalidDataException("Missing database path"));
                var asset = Catalog.For(database).Get(selected.GetString()!);
                character = asset.Locator?.Path ?? asset.Id;
            }
            result = NativeAnimationService.Search(resources,
                parameters.TryGetProperty("query", out var query) ? query.GetString() ?? "" : "", character,
                parameters.TryGetProperty("limit", out var limit) ? limit.GetInt32() : 200);
        }
        else if (method == "equipment-skill-window-bake")
        {
            // Bakes one SkillData-authored equipment window over the whole covered interval. The enter and end
            // triggers come from the parsed skill timeline; the native state machine, its exit-time transition
            // and the entered state own clip are the controller, so no gameplay state is fabricated.
            var resources = new GameResources(parameters.GetProperty("root").GetString() ?? throw new InvalidDataException("Missing game folder"));
            var database = SessionDatabase.Read(parameters.GetProperty("path").GetString() ?? throw new InvalidDataException("Missing database path"));
            var equipment = parameters.GetProperty("equipment").Deserialize<NativeEquipmentAnimationSelection>(WireJson.Options)!;
            var window = parameters.GetProperty("window").Deserialize<NativeEquipmentSkillWindow>(WireJson.Options)!;
            string asset = parameters.GetProperty("asset").GetString()!, resource = parameters.GetProperty("resource").GetString()!;
            result = NativeEquipmentAnimationService.BakeWindow(resources, database, asset, resource, equipment, window,
                parameters.GetProperty("duration").GetDouble(), parameters.GetProperty("sampleRate").GetDouble(),
                parameters.GetProperty("bodyClipId").GetString()!);
        }
        else if (method is "equipment-animation-plan" or "equipment-animation-bake")
        {
            var resources = new GameResources(parameters.GetProperty("root").GetString()!);
            var database = SessionDatabase.Read(parameters.GetProperty("path").GetString()!);
            var equipment = parameters.GetProperty("equipment").Deserialize<NativeEquipmentAnimationSelection>(WireJson.Options)!;
            var body = parameters.GetProperty("bodySelection").Deserialize<NativeAnimationSelection>(WireJson.Options)!;
            string asset = parameters.GetProperty("asset").GetString()!, resource = parameters.GetProperty("resource").GetString()!;
            result = method == "equipment-animation-plan"
                ? (object)NativeEquipmentAnimationService.Plan(resources, database, asset, resource, equipment, body)
                : NativeEquipmentAnimationService.Bake(resources, database, asset, resource, equipment, body);
        }
        else if (method == "animation-clips")
        {
            var resources = new GameResources(parameters.GetProperty("root").GetString() ?? throw new InvalidDataException("Missing game folder"));
            if (parameters.TryGetProperty("equipment", out var equipment))
            {
                var database = SessionDatabase.Read(parameters.GetProperty("path").GetString()!);
                result = NativeEquipmentAnimationService.Discover(resources, database, parameters.GetProperty("asset").GetString()!,
                    parameters.GetProperty("resource").GetString()!, equipment.Deserialize<NativeEquipmentAnimationSelection>(WireJson.Options)!);
            }
            else
            {
                string? character = null;
                if (parameters.TryGetProperty("asset", out var selected))
                {
                    var database = SessionDatabase.Read(parameters.GetProperty("path").GetString() ?? throw new InvalidDataException("Missing database path"));
                    var asset = Catalog.For(database).Get(selected.GetString()!);
                    character = asset.Locator?.Path ?? asset.Id;
                }
                result = NativeAnimationService.Discover(resources, parameters.GetProperty("resource").GetString()!, character);
            }
        }
        else if (method == "skill-search")
        {
            var resources = new GameResources(parameters.GetProperty("root").GetString()!);
            var database = SessionDatabase.Read(parameters.GetProperty("path").GetString()!);
            var owner = Catalog.For(database).Get(parameters.GetProperty("asset").GetString()!);
            string ownerPath = owner.Locator?.Path ?? "";
            Validation.Require(NativeAnimationService.CharacterToken(ownerPath) is not null && ownerPath.EndsWith("_uimodel.prefab", StringComparison.OrdinalIgnoreCase),
                "Skill search requires an imported character owner");
            string character = Path.GetFileNameWithoutExtension(ownerPath)[..^"_uimodel".Length];
            string prefix = "Json/SkillData/" + character + "_";
            string query = parameters.TryGetProperty("query", out var filter) ? filter.GetString() ?? "" : "";
            result = resources.LogicalNames.Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(name => new { name = name[prefix.Length..^5], resource = name }).ToArray();
        }
        else if (method == "skill-equipment-windows")
        {
            // SkillData-driven dedicated equipment scheduling: parse the real skill resource, project its weapon
            // animation actions onto equipment slots, then resolve those windows against each slot's own real
            // equipment controller. Unknown union bodies stop the parse and are reported instead of being skipped.
            var resources = new GameResources(parameters.GetProperty("root").GetString() ?? throw new InvalidDataException("Missing game folder"));
            var database = SessionDatabase.Read(parameters.GetProperty("path").GetString() ?? throw new InvalidDataException("Missing database path"));
            string asset = parameters.GetProperty("asset").GetString()!, resource = parameters.GetProperty("resource").GetString()!;
            string skill = parameters.GetProperty("skill").GetString() ?? throw new InvalidDataException("Missing skill logical name");
            var timeline = NativeSkillAnimation.Read(resources.GetBytes(skill), skill);
            // Every authored trigger is reported as authored. Slot identity is the authored weapon id, so a
            // trigger is only ever named from the parameter table its own controller authors.
            var authored = NativeSkillAnimation.EquipmentTriggers(timeline);
            var selections = new List<(NativeEquipmentAnimationSelection Equipment, string Resource, string[] Names)>();
            if (parameters.TryGetProperty("equipments", out var equipmentList))
                foreach (var row in equipmentList.EnumerateArray())
                    selections.Add((row.GetProperty("equipment").Deserialize<NativeEquipmentAnimationSelection>(WireJson.Options)!,
                        row.GetProperty("resource").GetString()!,
                        row.TryGetProperty("parameters", out var names) ? names.EnumerateArray().Select(value => value.GetString()!).ToArray() : []));
            else
                selections.Add((parameters.GetProperty("equipment").Deserialize<NativeEquipmentAnimationSelection>(WireJson.Options)!,
                    resource, parameters.GetProperty("parameters").EnumerateArray().Select(value => value.GetString()!).ToArray()));
            var slots = new List<object>();
            var unresolved = new List<object>();
            object? selectedSlotTriggers = null, selectedWindows = null;
            int namedInCoveredSlots = 0;
            foreach ((NativeEquipmentAnimationSelection selection, string selectionResource, string[] extraNames) in selections)
            {
                int slot = int.Parse(selection.SlotId[(selection.SlotId.LastIndexOf(':') + 1)..]);
                // Parameter identities come from this slot's own resolved controller chain; caller-supplied
                // names are an additional authored set, never a replacement for the slot's own identity.
                string[] controllerNames = NativeEquipmentAnimationService.ParameterNames(resources, database, asset, selectionResource, selection);
                var identities = NativeSkillAnimation.ParameterIdentities(controllerNames.Concat(extraNames));
                var slotTriggers = NativeSkillAnimation.ResolveTriggerWindows(authored, identities)
                    .Where(trigger => trigger.SlotId == slot).ToArray();
                // An incomplete root parse means the equipment windows were never evaluated; callers must show
                // that as "not evaluated", never as "this skill has no equipment animation".
                var windows = timeline.Complete && slotTriggers.Any(trigger => trigger.TriggerName is not null)
                    ? NativeEquipmentAnimationService.SkillWindows(resources, database, asset, selectionResource, selection,
                        slotTriggers.Where(trigger => trigger.TriggerName is not null).ToArray())
                    : [];
                namedInCoveredSlots += slotTriggers.Count(trigger => trigger.TriggerName is not null);
                foreach (var trigger in slotTriggers.Where(trigger => trigger.TriggerName is null))
                    unresolved.Add(new { trigger.SlotId, bits = trigger.ParamBits.ToString(), trigger.StartFrame, trigger.EndFrame,
                        reason = "bits-not-in-own-controller-names" });
                selectedSlotTriggers ??= slotTriggers;
                selectedWindows ??= windows;
                slots.Add(new { selection.SlotId, slot, resource = selectionResource, animatorId = selection.AnimatorId,
                    controllerId = selection.ControllerId, controllerNameCount = controllerNames.Length, slotTriggers,
                    unresolved = slotTriggers.Where(trigger => trigger.TriggerName is null)
                        .Select(trigger => new { trigger.SlotId, bits = trigger.ParamBits.ToString(), trigger.StartFrame, trigger.EndFrame }),
                    windows });
            }
            int[] covered = selections.Select(selection => int.Parse(selection.Equipment.SlotId[(selection.Equipment.SlotId.LastIndexOf(':') + 1)..])).ToArray();
            // A trigger on a slot no supplied equipment covers stays unevaluated: this diagnostic never claims
            // that every authored trigger of the skill was resolved.
            foreach (var trigger in authored.Where(trigger => !covered.Contains(trigger.SlotId)))
                unresolved.Add(new { trigger.SlotId, bits = trigger.ParamBits.ToString(), trigger.StartFrame, trigger.EndFrame,
                    reason = "slot-not-covered-by-supplied-equipment" });
            // The authored montage name is the only link from this skill to a body clip: it is resolved through
            // the character montage dictionary and the native manifest hash, never by matching resource names.
            object bodyClips = "owner has no native character animation config";
            var owner = Catalog.For(database).Get(asset);
            string ownerPath = owner?.Locator?.Path ?? "";
            // The native owner token is the character declaration id, so the uimodel suffix is stripped only
            // after the native prefab identity is confirmed.
            string? character = NativeAnimationService.CharacterToken(ownerPath) is not null
                ? Path.GetFileNameWithoutExtension(ownerPath)[..^"_uimodel".Length] : null;
            if (character is not null)
            {
                var declaration = NativeCharacterEquipment.Read(resources, character);
                if (declaration.AnimationConfigPath is not null)
                {
                    var map = NativeAnimationMontageConfig.Read(resources.GetBytes(declaration.AnimationConfigPath), declaration.AnimationConfigPath);
                    string[] keys = timeline.Elements
                        .SelectMany(element => new[] { element.MontageName, element.ForceSyncMontage }
                            .Concat(element.Actions.Select(action => action.AnimationName)))
                        .Where(key => !string.IsNullOrEmpty(key)).Select(key => key!).Distinct(StringComparer.Ordinal).ToArray();
                    bodyClips = new { map.Source, montagesComplete = map.MontagesComplete, map.ParsedBytes, map.UnparsedOffset,
                        map.UnparsedReason, boneWeightMasks = map.BoneWeightMasks, controllerHash = map.ControllerHash, referencedKeys = keys,
                        resolved = NativeAnimationMontageConfig.Resolve(resources, map, keys) };
                }
            }
            result = new { skill, complete = timeline.Complete, windowsScope = timeline.WindowsScope,
                incompleteReason = timeline.Complete ? null : "skill timeline not fully consumed: " + timeline.UnparsedReason,
                timeline = new { timeline.Name, timeline.TimelineActionCount, elements = timeline.Elements.Length, timeline.ParsedBytes, timeline.UnparsedOffset, timeline.UnparsedReason },
                triggers = authored, slots, slotTriggers = selectedSlotTriggers, windows = selectedWindows,
                unresolvedTriggers = unresolved,
                triggerDiagnostic = new { authored = authored.Length, namedInCoveredSlots, coveredSlots = covered,
                    note = "each slot is named from its own controller; triggers outside the supplied slots are unevaluated, never resolved" },
                bodyClips, weaponVisibility = NativeSkillAnimation.WeaponVisibility(timeline) };
        }
        else if (method == "animation-montages")
        {
            // The character AnimationConfig montage dictionary is the only authored link from a SkillData
            // montage name to a body clip: key -> AnimationClipAsyncInfo hash -> unique manifest resource.
            var resources = new GameResources(parameters.GetProperty("root").GetString() ?? throw new InvalidDataException("Missing game folder"));
            var database = SessionDatabase.Read(parameters.GetProperty("path").GetString() ?? throw new InvalidDataException("Missing database path"));
            var owner = Catalog.For(database).Get(parameters.GetProperty("asset").GetString() ?? throw new InvalidDataException("Missing asset"));
            string ownerPath = owner?.Locator?.Path ?? "";
            string? character = NativeAnimationService.CharacterToken(ownerPath) is not null
                ? Path.GetFileNameWithoutExtension(ownerPath)[..^"_uimodel".Length] : null;
            Validation.Require(character is not null, "Montage map requires a catalog character owner with a native prefab identity");
            var declaration = NativeCharacterEquipment.Read(resources, character!);
            Validation.Require(declaration.AnimationConfigPath is not null, "Character has no native animation config");
            var map = NativeAnimationMontageConfig.Read(resources.GetBytes(declaration.AnimationConfigPath!), declaration.AnimationConfigPath!);
            string[] keys = parameters.TryGetProperty("keys", out var requested)
                ? requested.EnumerateArray().Select(value => value.GetString()!).ToArray()
                : map.Montages.Select(montage => montage.Key).ToArray();
            result = new { source = map.Source, complete = map.MontagesComplete, parsedBytes = map.ParsedBytes,
                unparsedOffset = map.UnparsedOffset, unparsedReason = map.UnparsedReason,
                boneWeightMasks = map.BoneWeightMasks, controllerHash = map.ControllerHash, requested = keys.Length,
                montages = map.Montages.Select(montage => new { montage.Key, montage.Kind, montage.Offset, montage.EndOffset,
                    clips = montage.Clips.Select(clip => new { clip.Role, hash = clip.Hash.ToString(), hex = clip.Hash.ToString("x16"), clip.Length, clip.SampleRate, clip.IsLooping }) }),
                resolved = NativeAnimationMontageConfig.Resolve(resources, map, keys) };
        }
        else if (method == "animation-import")
        {
            var resources = new GameResources(parameters.GetProperty("root").GetString() ?? throw new InvalidDataException("Missing game folder"));
            var database = SessionDatabase.Read(parameters.GetProperty("path").GetString() ?? throw new InvalidDataException("Missing database path"));
            NativeAnimationSelection? selection = null;
            if (parameters.TryGetProperty("selection", out var selectedClip))
                selection = new NativeAnimationSelection(selectedClip.GetProperty("cab").GetString()!, selectedClip.GetProperty("pathId").GetString()!);
            string assetId = parameters.GetProperty("asset").GetString()!;
            if (parameters.TryGetProperty("equipment", out var equipment))
            {
                Validation.Require(!parameters.TryGetProperty("avatar", out _), "Equipment source binding does not accept an Avatar override");
                result = NativeEquipmentAnimationService.Import(resources, database, assetId,
                    parameters.GetProperty("resource").GetString()!, equipment.Deserialize<NativeEquipmentAnimationSelection>(WireJson.Options)!, selection!);
            }
            else
            {
                var indexedAsset = Catalog.For(database).Get(assetId);
                if (indexedAsset.Scene is null) {
                    var scene = GameCatalog.Scene(database, assetId, parameters.GetProperty("root").GetString());
                    database = database with { Assets = [indexedAsset with { Id = indexedAsset.Locator!.Path, Scene = scene }] };
                    assetId = indexedAsset.Locator!.Path;
                }
                result = NativeAnimationService.Import(resources, database, assetId,
                    parameters.GetProperty("resource").GetString()!, parameters.TryGetProperty("avatar", out var avatar) ? avatar.GetString() : null,
                    selection: selection);
            }
        }
        else
        {
            Validation.Require(method is "inspect" or "search" or "closure" or "scene", "Unsupported method");
            var stored = SessionDatabase.ReadValidated(parameters.GetProperty("path").GetString() ?? throw new InvalidDataException("Missing input path"));
            var database = stored.Database;
            var catalog = Catalog.For(database);
            result = method switch
            {
                "inspect" => new { database.GameVersion, assets = database.Assets.Length, database.CatalogSource, stored.Storage.FormatVersion, stored.Storage.PayloadBytes, stored.Storage.MaxPayloadBytes },
                "search" => catalog.Search(parameters.TryGetProperty("query", out var query) ? query.GetString() ?? "" : "", parameters.TryGetProperty("offset", out var offset) ? offset.GetInt32() : 0, parameters.TryGetProperty("limit", out var limit) ? limit.GetInt32() : 100, parameters.TryGetProperty("root", out var gameRoot) && !string.IsNullOrWhiteSpace(gameRoot.GetString()) ? SessionGameResources.Read(gameRoot.GetString()!) : null, parameters.TryGetProperty("kind", out var kind) ? kind.GetString() : null),
                "closure" => catalog.Closure(parameters.GetProperty("asset").GetString()!),
                "scene" => GameCatalog.Scene(database, parameters.GetProperty("asset").GetString()!, parameters.TryGetProperty("root", out var sceneRoot) ? sceneRoot.GetString() : null),
                _ => throw new InvalidDataException("Unsupported method")
            };
        }
        return JsonSerializer.Serialize(new { protocol = 1, id, ok = true, result }, WireJson.Options);
    }
    catch (OperationCanceledException) { return Failure(id, "cancelled", "Task cancelled at a safe checkpoint"); }
    catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException or OverflowException)
    { return Failure(id, exception is KeyNotFoundException ? "not_found" : "invalid_input", exception.Message); }
    catch (Exception)
    { return Failure(id, "internal_error", "The request could not be processed"); }
}

}

# ENDF2Blend process protocol 1 — SRED v3

Sora-Core is a local, framework-dependent backend. It does not download parsers or depend on Blender/Bridge. Source code is MIT; the distribution also contains separately documented native dependencies and notices. NuGet `pack` is unsupported; use the published distribution and its source/file manifests.

## Envelopes and task transports

Request: `{"protocol":1,"id":"request-id","method":"search","params":{...}}`.
Terminal response: `{"protocol":1,"id":"request-id","ok":true,"result":...}` or `{"protocol":1,"id":"request-id","ok":false,"error":{"code":"...","message":"..."}}`.

- `rpc`: synchronous newline-delimited request/response compatibility.
- `rpc-task`: one request with progress events, then one terminal response. A following `{"method":"cancel"}` is accepted for compatibility; `targetId` may also be supplied.
- `rpc-task-session`: sequential requests in one process. There is one active request and no pending work queue. Additional requests get `busy` immediately. Send the next request after the terminal response.
- Persistent cancellation is `{"method":"cancel","targetId":"request-id"}`. Missing or stale target IDs do not cancel another request; rejection is an `event:"control"` frame. Successful cancellation ends in terminal `error.code:"cancelled"` at a safe checkpoint.

All input lines, including controls, are bounded while reading to 1 MiB of UTF-8 bytes. Oversized input is discarded through newline before `invalid_input`; it is never accumulated as an unbounded string. Malformed UTF-8, duplicate keys, invalid JSON and invalid IDs fail explicitly. JSON nesting is limited to 64. EOF completes any accepted active request; EOF is not cancellation.

Progress frames contain `protocol`, `id`, `event:"progress"`, `stage`, nullable `completed`/`total`, and nullable `detail`. Null total is indeterminate. Counts are local to the named stage, not an estimated global percentage. Clients must consume events until the matching terminal response and must not treat backend data completion as completed Blender construction.

Managed table rows/values, mesh vertices/triangles, texture conversion, PNG rows/checksum, dependency resolution and commit boundaries have checkpoints. One native texture decode, one ACL worker unit, framework JSON parsing/serialization, filesystem flush and a terminal pipe write can delay interruption until their next safe boundary. No task-wide 60-second timeout is required. A cancel after atomic commit cannot undo the completed replacement.

## Database envelope and compatibility

Extension `.sredb`. Bytes 0–7: `SREDB\r\n\x1a`; bytes 8–11: little-endian uint32 version; bytes 12–19: little-endian uint64 payload length; bytes 20–51: SHA-256 of the exact payload bytes. The payload is UTF-8 JSON, maximum 256 MiB. Trailing bytes, checksum mismatch, duplicate/unknown fields and unsupported versions are rejected. RCM6 is unsupported.

| Version | Reader | Writer | Additions |
| --- | --- | --- | --- |
| 1 | supported | no | original assets/scenes; cannot contain resourceIndex/native clip metadata |
| 2 | supported | no | loaded CAB resourceIndex and native clip metadata |
| 3 | supported | current | catalogSource, asset locator/metadata, explicit item nodes and mesh coordinate contract |

New writes emit v3. Old strict readers must reject v3. Existing complete Scene databases without catalogSource remain standalone-compatible and are not silently rewritten. A formal catalog is identified by non-null `catalogSource`, even if an asset also has a cached Scene; formal imports require matching game metadata. Rebuild a separate unified catalog from the game root or read and rewrite an old sample to a separate output. Do not merge all decoded scenes into a full-game catalog.

`resourceIndex` remains a snapshot of loaded/decoded CABs, not a whole-game inventory. Source file paths are relative storage locators; CAB records distinguish indexed, decoded and unresolved data, with null meaning uninspected rather than empty. The SRED v2 resource-index contract does not imply RCM6 binary compatibility.

## Unified catalog and import capability

`catalogSource` records the native manifest version (document `gameVersion`), hash/revision, full manifest address/bundle coverage, metadata status, native table source records and per-table availability. Missing translation tables do not prevent physical indexing. Per-asset metadata explicitly distinguishes native-cn, missing-source, missing-row, missing-translation and not-mapped; internal names remain visible. Adding a previously missing table or changing a recorded digest invalidates the matching source inventory.

Asset IDs use native hash plus path SHA-256, never manifest ordinal or translated name. Same hash/path aliases retain all candidate bundles; the parser deterministically selects the lowest available bundle index. Dependency graph closure is a union for provenance when aliases exist, not a requirement that every alternative copy be installed. NPC IDs retain the exact declaration path; their locator additionally records the native LogicalResource source/digest to detect content revision changes.

Search rows include `databaseMode` (`indexed-game` or `standalone-scene`), `hasScene` for compatibility, `locator`, name `metadata`, and `capability`:

- cached: a complete decoded scene is available.
- requires-game: no matching root has been selected.
- requires-parse: a native parser can attempt the operation; deep structure and/or dependencies remain unknown.
- missing-dependencies/source-changed/version-mismatch: a specific known blocker requires repair/update.
- unsupported: no parser for this resource class.

Use `canAttemptImport` for “parse and import”. `canResolve` reports an available resolution path. Legacy `canImport` is retained as an alias for attemptability, not proof of successful deep parsing. NPC declarations are not disabled merely because their template/mesh dependencies have not yet been parsed. Item capability does not claim whole-family coverage: the parser is LOD0-only and validates renderer/skin/topology support on demand. Only separately recorded real samples are verified.

Game-source precedence is Persistent before StreamingAssets. Duplicate metadata is compared and counted as equivalent or shadowed. A physical chunk fallback is extracted against the selected native entry's digest. Queries check file/range presence, not complete chunk contents. Persistent sessions cache one database (file stat/header invalidation) and one read-only game directory (BLC SHA-256 plus file/directory/chunk stat invalidation). Actual scene parsing uses fresh resources and checks native payload digests. Whole-chunk hashing is deliberately not implied by a cache hit.

## Methods

- capabilities `{}`: product/version, actual assembly informational version, supported database versions, build source marker and methods.
- game-validate `{root,path?}`: manifest, source precedence/cache policy and optional catalog match.

  Task terminals now include `committed` (boolean): true means the database atomic
  replacement already occurred. Targeted cancellation is checked immediately before
  replacement under the same short state lock; a later cancel is rejected with control
  reason `too-late-committed` while the successful terminal still completes normally.
  A disconnected/killed process without a confirmed terminal has an unknown commit
  outcome; clients must reread the destination, never claim rollback. This transport
  field does not make scene-construction generators uncancellable.

  Normal precommit cancellation or failure runs backend finally cleanup of its
  newly created sibling temporary. Forced termination can prevent that cleanup;
  the frontend reports cleanup unconfirmed and does not automatically delete files.
  This forced-termination recovery branch is not complete and requires final validation.

  Atomic writes validate before creating the temporary, stream JSON through incremental
  SHA256 and a 256 MiB limiting writer, then backfill the unchanged 52-byte SRED header.
  Asset validation reports counts every 256 records; payload writing reports actual
  bytes at 64 KiB thresholds with unknown total, plus initial/final events. Verification
  reads and hashes in 64 KiB chunks with known byte count, then performs the complete
  existing JSON/schema validation before atomic replacement. Public `Write(Stream)`
  still supports nonseekable streams. Whole JSON parsing and some nested validation
  remain single operations; these checkpoints do not certify every stage as interruptible.

  Path-based writes use standard CreateNew, flush, reopen/verify and File.Move with
  replacement. The last cancellation check and replacement share the task commit lock.

  With `path`, `databaseCompatibility` is an additive contract (version 1):
  `contractVersion`, `mode` (`indexed-game`, `standalone-scene`, or `legacy-snapshot`),
  `databaseVersion`, `versionMatches`, `assetCount`, `cachedSceneCount`,
  `allAssetsHaveCachedScenes`, `canImportCachedScenes`, `canResolveIndexedAssets`.
  `matches` retains strict indexed manifest/version/metadata matching; a standalone
  scene database does not become a matching catalog. `versionMatches` compares only
  version labels, not resource compatibility. Without catalog provenance, validated
  cached Scenes remain independently importable even when labels differ; records
  without Scenes are not thereby importable. Zero cached Scenes is `legacy-snapshot`.
  `allAssetsHaveCachedScenes` requires a nonempty database and a Scene on every record;
  it does not claim full-game coverage. Indexed resolution still requires the existing
  strict match and per-asset capability checks. Native equipment/animation association
  compatibility is not certified by these fields. Validation never migrates or rewrites
  the input. No-path validation returns null compatibility. Older response schemas keep
  the frontend's strict match default; malformed or unknown compatibility contracts fail closed.
- database-build `{root,path}`: full index build/update. Write sibling temporary file, flush, reopen/validate, check cancellation, atomically replace. Precommit failure/cancel preserves the old database; postcommit cancellation cannot undo replacement.
- inspect `{path}`: gameVersion, asset count, catalogSource.
- search `{path,root?,query?,kind?,offset?,limit?}`: total/rows pagination; name, internal name and native English name search. Kinds include character, npc, weapon, equipment (character-declared dedicated equipment), resource and bundle.
- closure `{path,asset}`: reachable dependency union and missing catalog IDs.
- scene `{path,asset,root?}`: standalone cached Scene or on-demand native parse. It does not write parsed geometry into the unified catalog.
- authored face DTOs carry per-control and per-preset `classification {category,rule,source,confidence}` derived from the native `partType` bit (controls) or the native `skeletalmorphanim` directory group (presets); unrecognized values stay `UNKNOWN`/`unknown` and Core never infers a part from the control name.
- material NPR descriptors carry `classification {category,rule,source,confidence}` derived from the resolved native shader identity (`m_ParsedForm.m_Name`); unmapped or unresolved shaders stay `unclassified`/`unknown` with the resolution status as source, and Core never substring-guesses a class from the material name. The field is native provenance only; it does not encode the frontend preview material mode or whether the material imports. Legacy descriptors without the field stay valid (`null`).
- character-equipment `{root,path,asset}`: source-owned StaticWeaponData declarations separately from dynamic general-weapon declarations. No guessed ownership from defaultWeaponId.
- equipment-assembly `{root,path,asset,includeOwner?}`: resource scenes, independent slots, native attachment paths/IDs, idle/fight state frames and explicit gaps. With includeOwner true, returns `{scene,equipment}`. Default returns only assembly.
- pose-map `{root,path,asset}`: exact native Avatar body-slot/path/hash to scene-bone mapping; pose application is frontend-owned.
- animation-search/animation-clips/animation-import: existing compatibility methods described in ANIMATION-API.md. Source/path identity is checked; parsing coverage is not expanded by catalog lookup.
- map-status/map-read: placeholders. `supported:false` and null document must never be imported as an empty map.

## Scene and equipment coordinate contract

Scene `nodes` are optional `{id,name,parent,sourcePath,localMatrix}` records, parent before child. Matrices are standard row-major 4×4 arrays in Core's Blender-coordinate scene space. Mesh `node` plus `coordinateSpace:"node"` means local vertices need that accumulated node transform exactly once. `coordinateSpace:"scene"` means skinned vertices already use scene coordinates; the node is provenance and must not be multiplied again. Rigid items do not acquire fabricated humanoid bones.

Equipment attachment matrices are relative to the **head** frame of the imported owner bone:

`ownerBoneHeadRestMatrix @ localMatrix = nativeMountWorldInScene @ declaredScale`.

They exclude resource node-root matrices and Blender BONE-parent tail/length behavior. The frontend must account for that convention exactly (or use an equivalent constraint), preserve the invariant through NPR basis conversion, and attach the whole item instance once. Source helper chains and explicit legacy fields are identified. Unimplemented damping, binary state commands or events remain gaps, not silently simulated behavior. Missing required resources or genuinely ambiguous attachment targets must fail the import transaction rather than creating items at the origin.

## Paginated animation and general weapons

`animation-search-page {root,path?,asset?,query?,offset?,limit?,category?}` returns total/offset/limit/rows; classifications carry rule/source/confidence and remain inferred or unclassified.

`compatible-weapons {root,path,asset,query?,offset?,limit?}` returns the character's general-weapon type candidates, each graded by its own evidence (`rows[].adaptation {status,confidence,rule,source,message}`). `confirmed-native-type` means a WeaponBasicTable row references that exact resource path. `inferred-native-base-type` means no row references the resource but it is named `<declared-weapon-id>_refined.prefab` and its two available native associations (the name-derived base id and the base resource path) agree on the type; ownership is reported as `consistent` or `ambiguous` and is never claimed confirmed, and conflicting association types stay `unknown-native-weapon-type` in the top-level `unknown` list. The candidate set is restricted to catalog `kind=weapon`; character-declared dedicated equipment is excluded structurally (`dedicatedExclusion`). This contract is type adaptation only and does not assert a verified mount. `weapon-assembly {root,path,asset,weapon}` returns scene, genericSlots, compatibility, gaps, canBindStates and status. Dedicated, non-weapon, unknown-type and mismatched-type selections return scene:null and blocked status (`dedicated-equipment-not-general`, `not-a-general-weapon`, `unknown-weapon-type`, `incompatible` or `inferred-type-mismatch`). A matching inferred candidate parses its own resource and resolves the owner's native dynamic slots: `inferred-base-type-mount-verified` requires a parsed scene, nonempty slots and bindable visible slots in both idle/fight states; otherwise compatibility remains `inferred-type-candidate-not-mount-verified`, with the parsed scene/slots and `blocked-general-weapon-mount` status. Resource parse errors use the existing RPC failure response. Type recognition and any ambiguous base ownership remain inferred even when the mount is verified. Target role is per idle/fight state because a general weapon can move from an owner mount to a dedicated-equipment mount. targetKind is bone-head, scene-node or owner-main-model-root, with targetDedicatedSlotId and parentBoneIndex or parentNodeId/sourcePath where applicable. A dynamic owner-main-model-root mount targets the owner and has no targetDedicatedSlotId. The same strict matrix invariant applies in the declared parent space; these are separate instances and are never relabeled as dedicated equipment.

## Dedicated equipment clip sampling

`animation-clips` and `animation-import` accept an optional strict `equipment:{slotId,resourceId,animatorId,controllerId}` selector together with `root,path,asset,resource`. In this branch `asset` is the catalog owner and `resource` is its declared dedicated prefab. The selected clip is a native controller reference, not a client-supplied skeleton mapping. Import requires `selection:{cab,pathId}` and returns existing `clip,bones` plus equipment identity/frame/binding proof. The native61 character branch is unchanged when `equipment` is absent. See ANIMATION-API.md for exact basis TRS, quaternion order, full native grid bounds, fail-closed binding diagnostics and the explicit exclusion of controller/event/visibility/damping runtime.

Equipment proof contract v2 is additive: capabilities and equipment rows/proofs carry `native-equipment-clip-proof-v2`; discovery includes complete `bindingSchema` plus its exact native clip/manifest source. Unmapped/ambiguous schema paths are explicitly null, unsupported bindings remain visible, and import still fails rather than returning partial data. The frontend compares full schema attributes/identities and never parses native path hashes itself. See ANIMATION-API.md.
# Validated database budget (additive fields)

Successful `inspect`, `database-build`, and `game-validate` with a database path
return optional top-level `formatVersion`, `payloadBytes`, and `maxPayloadBytes`.
These integer byte counts describe the validated UTF-8 JSON payload, excluding
the 52-byte container header; the current limit remains 268435456 bytes.
Remaining capacity is `maxPayloadBytes - payloadBytes`. They are not scene size,
asset coverage, compressed file size, or a memory usage estimate.

The reader reports the original header version and actual payload length only
after checksum, format and document validation. A build reports the verified
temporary container's budget only after atomic replacement succeeds. Invalid,
oversized, cancelled or failed writes still return their existing error, never a
success budget; the prior output remains protected by the existing transaction.
Root-only validation returns null budget fields. Older servers may omit all
three fields; clients must show unknown and must not retain a previous database's
budget. Unknown/malformed optional budget values must not prevent old-server use.
No container version or payload limit changed; v1-v3 readers/standalone Scene
compatibility and explicit RCM6 rejection retain their existing scope.

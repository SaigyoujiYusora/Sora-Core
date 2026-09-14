# Native animation process API

The protocol remains line-delimited JSON with `protocol: 1`. Responses use the
same request ID and the existing `ok` / `result` or `error` envelope.

| Method | Parameters | Result |
| --- | --- | --- |
| `animation-search` | `root`, optional `query`, `limit` (1–1000), optional selected database `path` + `asset` | Resource rows `{id,path,label}` |
| `animation-clips` | `root`, exact `resource`, optional database `path` + `asset` | Native subclips `{resourcePath,cab,pathId,name}` |
| `animation-import` | `root`, database `path`, selected `asset`, exact `resource`, optional `selection: {cab,pathId}`, optional exact `avatar` resource | `{clip,bones}` |

`root` explicitly selects the game installation. Bone identities and rest frames
come from the selected decoded database scene. A stored resource manifest hash
must match the current installation. Import retains the complete source identity
and custom scalar data inside `clip.native`; it does not write the input database.

Search combines addressable animation paths and native Animator controller clip
references for the selected character. UI controller clips may exist only as
dependencies, without standalone manifest addresses. Listing those clips requires
the selected character. Import verifies that the selected CAB and path ID belong
to its controller and exact container path. A path ID is a signed 64-bit decimal
string, preserving precision across JSON implementations.

Multi-clip FBX resources require explicit selection. A single addressable clip can
still be imported without selection for compatibility. Unknown, ambiguous or
unrelated identities produce errors; Core never chooses an arbitrary first clip.

CLI equivalents:

```
Sora-Core animation-search <game-root> <query> [<character-prefab>]
Sora-Core animation-clips <game-root> <resource> [<character-prefab>]
Sora-Core animation-export <game-root> <input.sredb> <asset> <resource> <output.sredb> [<cab> <path-id>]
```

Export adds the new clip and merges newly inspected resource metadata into the
existing SRED snapshot. It preserves existing decoded records and rejects
inconsistent snapshots or duplicate clip names before atomic output replacement.
Discovery does not imply that every native animation layout is supported; the
binding and decoder contracts remain checked during import.

## Paginated discovery

`animation-search-page` accepts `{root,path?,asset?,query?,offset?,limit?,category?}` and returns `{total,offset,limit,rows}`. Limit is 1–1000; total is computed before slicing, including lists larger than the legacy 200-item default. Categories are all, unclassified, idle, move, attack, skill, interaction. Each row includes `classification:{category,rule,source,confidence}` and `discoverySource`. Current categories are explicitly inferred from verified resource paths/name tokens, not represented as native authored semantic tags; unmatched items remain unclassified. This does not add animation decoding formats. Legacy animation-search retains its capped list for compatibility; new UI browsing should use pages.
## Dedicated equipment single-clip route

The optional `equipment` object on `animation-clips` and `animation-import` selects this route. Without it, the existing character/native61 behavior is unchanged. Equipment requests use `path` (unified catalog), matching `root`, `asset` (catalog owner ID), `resource` (exact equipment prefab path), and:

```json
{"equipment":{"slotId":"owner:equipment:10","resourceId":"address:...","animatorId":"CAB-...:signed-id","controllerId":"CAB-...:signed-id"}}
```

Obtain these identities from the owner's `equipment-assembly` resource/slot records. Core rechecks the owner catalog entry, matching manifest/metadata, source character declaration, exact slot/resource pair, prefab Animator and referenced controller chain. The client supplies no bone mapping, rest frame or Avatar override. Discovery retains `{resourcePath,cab,pathId,name}` and adds `equipment`, `originalSourceId`, `controllerChain` and status `referenced-clip; sampling-and-binding-not-yet-validated`. Discovery is not a playback capability claim.

Import additionally requires `selection:{cab,pathId}` from that discovery. Its result is `{clip,bones,equipment}`. The first two fields reuse existing records. `equipment` contains source identity, clip/controller provenance, sample rate/count, the exact `times` array and `{pathHash,attribute,bone,sourcePath}` bindings. `clip.native.source` records the equipment prefab path plus the exact clip CAB/path ID and manifest hash. No input database is written.

Tracks are bone-local **basis TRS relative to the returned native Rest**, using channels `location`, `rotation`, `scale`. Rotation values follow the existing `[x,y,z,w]` wire order. All returned bones receive all three tracks; unanimated channels retain source Rest rather than inheriting a user's currently posed bones. The frontend must verify its selected dedicated rig's equipment/controller provenance and complete bone source-path/index/Rest mapping before applying these keys. It must retain the existing NPR object-frame conversion and parent/attachment conventions.

Sampling is the reviewed non-ACL generic streamed-cubic/packed16-position/constant layout, at the authored `m_SampleRate` only. Dense sample rate must match. Zero and the endpoint are included; this narrow version rejects non-integral/off-grid endpoints instead of inventing a final interpolation or silently truncating. Limits are 4,096 bones, 100,001 sample times and 1,000,000 emitted TRS keys. Unsupported bindings report their path hash and attribute; unknown or ambiguous paths, outside-rig targets, unsupported data, invalid quaternion/shear or source mismatches fail the whole clip. No partial clip or zero-motion fallback is returned.

Task transports retain progress and safe cancellation (`resolve-equipment-animation`, native item parse/decode phases, `sample-equipment-animation`). Cancellation during sampling throws before a successful result is returned. The frontend should preserve its existing scene until a complete, validated clip has been received.

This route samples a single referenced clip. It does not execute controller transitions, blend trees, WeaponVisible/WeaponAnim events, visibility, root-motion policy or damping. Its proof scope states these omissions. It is not a claim that the bow's multi-prong Fight appearance has been fixed.

## Complete discovery schema (proof contract v2)

Additive equipment DTO fields advertise `equipmentAnimation.proofContract = "native-equipment-clip-proof-v2"` in capabilities; the sampling contract remains `non-ACL native frame grid`. Existing no-equipment/native61 responses and request parameters are unchanged.

Each equipment discovery row now includes `proofContract`, `bindingSchemaSource:{resourcePath,cab,pathId,manifestHash}` and **every** native generic binding in `bindingSchema:[{pathHash,attribute,typeId,customType,isPPtrCurve,sourcePath,resolution}]`. Core resolves path hashes against the selected Animator's exact native hierarchy. Resolution is `native-path`, `unmapped-path-hash` or `ambiguous-path-hash`; unresolved paths remain null. Unsupported or duplicated bindings are preserved in discovery rather than discarded. Discovery is still referenced/unvalidated, and such clips fail import if unsupported. The selected row's existing equipment identity supplies `animatorSourcePath` and `manifestHash` from the same Core source resolution.

Import proof adds the same `proofContract`; its existing `bindings` are compared by the frontend with the selected discovery schema on the full `(pathHash,attribute,sourcePath)` set, not just the distinct paths or output track count. The frontend does not compute Unity hashes or infer missing attributes. `bindingSchemaSource` and `clip.native.source` must agree with the selected clip and catalog identity. All Core binding resolution and sampling rules are unchanged.

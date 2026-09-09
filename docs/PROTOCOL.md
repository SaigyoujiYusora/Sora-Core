# ENDF2Blend process protocol 1

Sora-Core is a standalone executable. `Sora-Core rpc` reads one JSON object per
stdin line and emits one response per line on stdout. EOF exits. No networking,
runtime downloads, native plugins, Blender imports or frontend dependencies.

Request: `{"protocol":1,"id":"1","method":"capabilities","params":{}}`.
Response: `{"protocol":1,"id":"1","ok":true,"result":{...}}` or
`{"protocol":1,"id":"1","ok":false,"error":{"code":"...","message":"..."}}`.
Unsupported methods, protocol versions, duplicate keys and invalid data fail explicitly.
Limits: 1 MiB request, 256 MiB database payload, 64 JSON nesting levels.

The process serves capabilities, database inspection/search and scene lookup. Each
request names its input file explicitly. The frontend passes an executable path
selected by the user and uses argument arrays without a shell. It does not fetch,
install or redistribute Sora-Core. Asset paths stored in a database are metadata;
opening a database never executes them or loads assemblies.

# Sora Endfield Database 2

Extension `.sredb`. Bytes 0..7 are `SREDB\r\n\x1a`; bytes 8..11 are little-endian
uint32 version 2; bytes 12..19 are little-endian uint64 payload byte length;
bytes 20..51 are SHA-256 of payload; bytes 52 onward are a UTF-8 JSON database
document. No trailing data, comments, NaN, duplicate keys or unknown version.
Writers validate first and write to a temporary sibling, then atomically replace.
Canonical JSON property ordering is not required; the hash covers exact stored bytes.

The reader accepts versions 1 and 2; the writer emits 2. Version 1 retains the
original `gameVersion` / `assets` document and cannot contain `resourceIndex`, even
as null. Reading v1 does not invent source metadata. Unknown versions and unknown
fields are rejected. Old v1 readers cannot consume v2; RPC `capabilities` reports
`databaseVersions: [1,2]`. The process protocol remains version 1.

Version 2 adds optional `resourceIndex` to `DatabaseDocument`, without changing
scene transport. `EndfieldResourceIndex.cs` defines source root, manifest hash and
revision (which may be empty in native data), source files and CAB records. Each file references a relative BLC index
path and the existing `LogicalResource` model (logical name, physical chunk,
offset/length, encryption/nonce and source digests). CAB `file` is a zero-based
index into this file array; `entry` uses the existing `VfsEntry` model, whose
offset is in the decoded archive, not the physical encrypted chunk. Dependencies
are CAB identity strings and must name either an indexed or unresolved row.

CAB status is `decoded`, `indexed`, or `unresolved`. A decoded row has complete
class-ID, container-path and external-CAB dependency arrays for that document.
An indexed row has a locator but null metadata arrays. An unresolved row has
neither locator nor inspected metadata. Empty decoded arrays mean inspected and
empty; null means not inspected. Identity matching is case-insensitive for CABs;
container paths preserve exact spelling. The resource snapshot covers the loaded
closure, not the whole installation. `import-character` attaches it after native
extraction; `manifest` does not invent CAB locations from bundle indices.

The source root is informational; relative locators cannot contain parent steps,
absolute paths, or backslashes. Opening a database never follows these paths.
Relocating/reopening native data requires an explicit resource-root decision;
there is no automatic old-folder probing. V2 has no RCM6 byte compatibility or
Unreal adapter. See `SRED-V6-MAPPING.md` for the relevant historical structures.

Records use stable string identities and explicit string dependencies. Missing
references remain visible; traversal terminates with cycles and has a finite limit.
Blender scene payloads use meters, right-handed Z-up coordinates, XYZW quaternion
storage, explicit bone-local animation channels, triangle indices, rest-space vertex
positions and a parent-before-child skeleton. This is a new exchange contract, not
a claim that Unity raw data already uses these conventions.

Bone head and tail are in armature object space; `roll` is Blender's rest-bone roll
in radians (optional, defaults to zero). Local +Y follows head to tail. Animation
channels are Blender pose `matrix_basis` deltas relative to this complete rest basis,
not raw Unity local TRS. Location defaults to zero, rotation to XYZW (0,0,0,1), scale
to one. Native converters must explicitly transform game rest matrices and curves
into this basis before creating a SceneDocument. A rolled two-bone fixture verifies
the bridge contract; it does not establish native humanoid retargeting coverage.
Bone names must fit Blender's 63-byte UTF-8 limit and are rejected before import if
they would be truncated. No silent skeleton-name remapping is performed.
## Native clip metadata in SRED v2

`ClipRecord.native` is optional on read. When present it stores source
`{resourcePath,cab,pathId,manifestHash}`, custom scalar tracks and diagnostics.
Source may be null when an in-memory conversion has no resolved native origin.
Custom tracks contain `{path,typeId,customType,attribute,sampleRate,values}`;
native path and attribute are unsigned 32-bit identities, and the source object
pathId is a signed 64-bit decimal string. Scalar time is index/sampleRate.
Rates/counts must cover the same interval as the clip; all values are finite;
identity tuples are unique and scalar samples share the scene animation budget.

Old v1 and v2 clips without this field remain readable. V1 explicitly rejects a
native clip field. V2 writers persist embedded metadata automatically. Earlier
v2 readers with a strict older record schema reject the new field; this is not
a claim of forward compatibility with those implementations.

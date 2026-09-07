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

# Sora Endfield Database 1

Extension `.sredb`. Bytes 0..7 are `SREDB\r\n\x1a`; bytes 8..11 are little-endian
uint32 version 1; bytes 12..19 are little-endian uint64 payload byte length;
bytes 20..51 are SHA-256 of payload; bytes 52 onward are a UTF-8 JSON database
document. No trailing data, comments, NaN, duplicate keys or unknown version.
Writers validate first and write to a temporary sibling, then atomically replace.
Canonical JSON property ordering is not required; the hash covers exact stored bytes.

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

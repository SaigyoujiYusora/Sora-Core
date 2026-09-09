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

# Build Sora-Core

Use the .NET 10 SDK. The verified Windows x64 environment uses SDK 10.0.400
and host/runtime pack 10.0.11. Blender is not a build or runtime dependency.

```
dotnet build src/Sora.Cli/Sora.Cli.csproj -c Release
dotnet run --project tests/Sora.Tests -c Release
dotnet publish src/Sora.Cli/Sora.Cli.csproj -c Release -r win-x64 --self-contained false -o artifacts/publish/win-x64
```

On Windows the ordinary build produces `src/Sora.Cli/bin/Release/net10.0/Sora-Core.exe`.
The publication directory contains `Sora-Core.exe`, its managed assemblies,
configuration, the two native decoder libraries, notices and native ACL source.
Distribute that directory together; copying only the executable is insufficient.
Select its `Sora-Core.exe` in Endfield-Bridge preferences. The executable works
independently of Blender; `Sora-Core.exe rpc` exposes the process API.

This is a framework-dependent release and requires a separately installed .NET 10
runtime. Native extraction currently targets Windows x64. Self-contained and
single-file releases are not part of the audited distribution.

The generated executable uses Microsoft's MIT-licensed apphost component.
Preserve `licenses/dotnet-apphost` when distributing the executable. A different SDK/host pack must have its component
metadata, template hash and notices checked before a new release is published.
The SDK installer's top-level license is not substituted for the component's
own explicit NuGet license.

## Source-only repository

Prebuilt native DLLs are not tracked in Git. The C# solution can be compiled from
a fresh checkout without them; native decoding, the full test harness, and a
complete application publication require separately prepared Windows x64 libraries.
The local native/ directory is ignored by Git.

- ACL source is included in native-source/acl/source. With Python 3 and the pinned
  LLVM-MinGW compiler described in native-source/acl/BUILD.json, run
  `python native-source/acl/rebuild.py --compiler <clang++.exe> --output native/win-x64/Sora.Acl.dll`.
  The script verifies the compiler, source inventory, and rebuilt DLL hash.
- Texture decoder source is maintained at
  https://github.com/KiruyaMomochi/Texture2DDecoder, pinned commit
  c974dbda1209a6af34cb03941fa0b999627c3623. Follow that source version's build
  instructions to produce Texture2DDecoderNative.dll and place it in native/win-x64.
  TexturePixels.cs verifies the runtime DLL hash; a different build must be reviewed
  and its expected hash updated deliberately before use.

Publishing reports an error if either native library is missing. Preserve LICENSE
and the complete licenses/ directory when distributing a locally built application.

# Sora-Core

Sora-Core 是 ENDF2Blend 的本地后端，负责读取《明日方舟：终末地》游戏资源，并通过进程 RPC 为 Blender 插件 Endfield-Bridge 提供数据。它也可以作为独立命令行程序使用。


## Credits

感谢以下开源项目及其贡献者：

- **[Texture2DDecoder](https://github.com/KiruyaMomochi/Texture2DDecoder)**：提供纹理解码支持（[版权与许可声明](licenses/TextureDecoder-NOTICES.txt)）。
- **[AnimeStudio](https://github.com/Escartem/AnimeStudio)**：提供动画解码相关代码（[许可证](licenses/AnimeStudio-MIT.txt)）。
- **[Animation Compression Library (ACL)](https://github.com/nfrechette/acl)**：提供压缩动画解码支持（[版权与许可声明](licenses/ACL-NOTICES.txt)）。

[Crunch](https://github.com/richgel999/crunch) 库致谢声明：

> Crunch Library Copyright (c) 2010-2016 Richard Geldreich, Jr.

## License

本项目采用 [MIT](LICENSE)。其他原生依赖的许可见 [licenses](licenses)。

# Plain Craft Launcher

## 本分支：内置联机与下载恢复

日常使用现在优先采用 **官方正式版功能补丁**，保留官方程序主体，不再依赖缺少私有模块的源码重编译版。
统一工具可同时恢复下载/整合包并接入启动页联机，用自己的官方 `2.99.0.1` EXE 构建：
`./tools/Build-OfficialPatch.ps1 -OfficialExe 'C:\path\Plain Craft Launcher 2.exe' -MakeSample`。
工具在 `dist/PCL-Link-PatchTool`，本地副本在 `dist/PCL-Official-Link`；[补丁说明与还原方法](docs/OFFICIAL-LINK-PATCH.md)。
工具发布包不包含官方 EXE、密钥、配置、登录缓存或用户游戏文件。下面的源码版保留作界面与后台开发参考。

第三方独立修改版，保留 PCL 原有界面与署名。新增原生联机页，随包组网后台、Minecraft Java 局域网世界自动发现；恢复原版下载、整合包安装及相关入口。

- [使用、构建与验证范围](docs/INTEGRATED-LINK.md)
- [下载与整合包恢复清单](docs/RESTORED-FEATURES.md)
- 构建：`./tools/Build-Link.ps1`；产物在 `dist/PCL-Link`。
- 第三方内核的公开再分发许可仍需确认，本地产物不代表已经获得发布授权。

---

[![Stars](https://img.shields.io/github/stars/Meloong-Git/PCL?style=flat&logo=data:image/svg%2bxml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZlcnNpb249IjEiIHdpZHRoPSIxNiIgaGVpZ2h0PSIxNiI+PHBhdGggZD0iTTggLjI1YS43NS43NSAwIDAgMSAuNjczLjQxOGwxLjg4MiAzLjgxNSA0LjIxLjYxMmEuNzUuNzUgMCAwIDEgLjQxNiAxLjI3OWwtMy4wNDYgMi45Ny43MTkgNC4xOTJhLjc1MS43NTEgMCAwIDEtMS4wODguNzkxTDggMTIuMzQ3bC0zLjc2NiAxLjk4YS43NS43NSAwIDAgMS0xLjA4OC0uNzlsLjcyLTQuMTk0TC44MTggNi4zNzRhLjc1Ljc1IDAgMCAxIC40MTYtMS4yOGw0LjIxLS42MTFMNy4zMjcuNjY4QS43NS43NSAwIDAgMSA4IC4yNVoiIGZpbGw9IiNlYWM1NGYiLz48L3N2Zz4=&logoSize=auto&label=Stars&labelColor=444444&color=eac54f)](https://github.com/Meloong-Git/PCL/)
[![Issues](https://img.shields.io/github/issues/Meloong-Git/PCL?style=flat&label=Issues&labelColor=444444&color=1F883D)](https://github.com/Meloong-Git/PCL/issues)
[![哔哩哔哩](https://img.shields.io/badge/动态-BiliBili-00A4DB?style=flat&labelColor=444444&logoSize=auto)](https://space.bilibili.com/11343203/dynamic)
[![爱发电](https://img.shields.io/badge/赞助-爱发电-946ce6?style=flat&labelColor=444444&logoSize=auto)](https://meloong.com/afd/a/LTCat)

包含 PCL 的大部分源代码，包括 UI 库、动画模块、下载模块、Minecraft 启动模块等！<br/>
代码并不是实时更新的，它会在每次 PCL 发布更新时同步一次。<br/>
非常感谢社区的大力支持！

<br/>

### 相关资源
- [PCL 下载](https://meloong.com/pcl)：下载可免费使用的正式版 PCL！
- [PCL 功能投票](https://github.com/Meloong-Git/PCL/discussions/2)：来参加投票吧，开发者会优先处理票数较高的项目！
- [帮助文档库](https://github.com/LTCatt/PCL2Help)：PCL 内置帮助文档的存储库

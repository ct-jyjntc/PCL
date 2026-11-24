# 下载与整合包功能恢复

参考项目：https://github.com/ShiraiFyuya/pcl2-patch
检查时的提交：`abaa77cc0f3ea34852d845f9b0126560f5d64e18`。
当前 PCL 基础提交：`93ede5d`，版本 `2.99.0.1`。

该项目使用 Mono.Cecil 修改发行 EXE 的 IL。本仓库已有相关完整源码，故不叠加二进制补丁，也不复制 GPL 补丁引擎。
将 `ModSecret.RestrictMcDownload` 恢复为 `False`，让原有分支重新生效：

- Minecraft 版本列表使用真实 `DlClientListLoader`。
- 下载页恢复「原版游戏」入口及默认选择。
- 启动页、版本选择页、空状态、模组禁用页恢复下载入口。
- 版本修改页恢复 Minecraft 下载/修复相关卡片。
- 恢复本地选择、拖入 ZIP/MRPACK 和启动目录 modpack.zip/modpack.mrpack 自动安装。
- 恢复 CurseForge、HMCL、MMC、MCBBS、Modrinth 和带启动器包的已有分派；未知包保留原有压缩包兜底。
- 整合包资源版本行优先「安装」，仍保留「另存为」操作。
- 游戏更新提醒里的下载按钮恢复。

`SystemSystemUpdate` 默认值改为 `3`（关闭启动器更新提示）。源码版的 `UpdateCheckByButton`、`UpdateRestart` 已为不更新的桩，没有增加自动下载官方版本的实现。
未尝试补回上游没有公开的 OAuth client ID、CurseForge API key、签名密钥或鉴权代码。
微软登录与 CurseForge 下载如需专有凭据，仍按原代码使用 `PCL_MS_CLIENT_ID`、`PCL_CURSEFORGE_API_KEY` 环境变量配置。

RAR 支持仍与原实现相同：部分 ZIP 文件使用 RAR 后缀时可以识别，真正的 RAR 文件可能需要重新压缩为 ZIP。
没有宣称恢复与当前 PCL 无关的 PCL-CE 地区判定，也没有恢复已废弃的 EasyTier 联机实现；联机使用本项目独立的内置模块。

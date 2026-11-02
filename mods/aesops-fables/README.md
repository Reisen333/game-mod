# Aesops_Fables 语音中文字幕 v1.0

为游戏的日语语音和视频对白添加简体中文字幕，直接显示在游戏画面内，随播放位置同步。

**[下载分享版 ZIP](https://github.com/Reisen333/game-mod/releases/download/aesops-fables-zh-v1.0/Aesops_Fables_ZH_Subtitles_v1.0.zip)** · [Release 页面](https://github.com/Reisen333/game-mod/releases/tag/aesops-fables-zh-v1.0) · [完整安装教程](package/先读我-安装教程.txt)

## 安装

1. 关闭游戏，下载并解压上方的分享版 ZIP。
2. 将其中的 `resources` 文件夹，以及 `安装中文字幕.bat`、`卸载中文字幕.bat`，一起复制到 `Aesops_Fables.exe` 所在目录。同名文件夹选择合并。
3. 双击 `安装中文字幕.bat`，看到安装完成提示后，按原来的方式启动游戏。

安装脚本应与游戏 EXE 在同一层。若你原本通过 MTool 启动，继续使用你自己的启动方式即可。

适用 Windows 10/11，无需安装 Python、语音识别模型或其他工具；安装前请预留约 2 GB 可用空间。首次备份和校验较大的资源包可能需要几十秒。

## 适配版本

- 已校验的游戏版本：`Ver.product-1.23`。
- 原始 `resources/app.asar` 的 SHA-256：`aa5854beba136bf04275793ad2d4654dc5eff1e166f7b9748a63ffd67b8c9fd4`。
- 安装器会自动校验资源包。其他版本或已被其他资源补丁修改的游戏会停止安装。

## 调整和卸载

关闭游戏后，编辑 `resources/zh_subtitles/config.json`，重启游戏生效：

| 设置 | 默认值 | 作用 |
| --- | --- | --- |
| `enabled` | `true` | 改为 `false` 隐藏字幕 |
| `fontSize` | `28` | 字号，例如改为 `32` |
| `bottomOffset` | `120` | 距画面底边的高度，数值越大越靠上 |

卸载时先关闭游戏，再双击 `卸载中文字幕.bat`。它会恢复原版资源，保留存档、外置文字汉化、字幕文件和备份；重新运行安装脚本可以再次启用。

原版备份位于 `resources/zh_subtitles_backup/app.asar.original`。请保留该备份及 `resources/zh_subtitles/install-state.json`，以便自动恢复。若资源包在安装后又被其他补丁修改，卸载脚本会停止覆盖。

## 覆盖和已知问题

共处理 396 个音视频文件，生成 **574 条字幕**，涉及 218 个有可辨对白的文件。175 个文件未检出可用对白，另有 3 段含混短句暂未填入，见[待复核清单](package/resources/zh_subtitles/待复核清单.txt)和[逐文件记录](package/resources/zh_subtitles/coverage-report.json)。

本版基于本地自动转写和文本校订，可能存在误听、误译或断句问题，尚未逐句人工听校，也没有完整游玩全部分支。已验证音视频播放、暂停、循环、跳转、菜单遮挡、原生快照读档、窗口缩放及换路径启动；MTool 的全部注入功能未重新测试。

## 文件说明

- `downloads/`：已验证的分享版 ZIP 和 SHA-256 校验文件。
- `package/`：与 ZIP 完全对应的可编辑补丁文件、双击安装/卸载脚本和教程。
- [发布介绍](package/发布介绍.txt)：分享或发帖时可复制使用的介绍。

安装器会备份原始资源包，在 `index.html` 增加一行字幕加载入口。分发包仅提供字幕与安装文件，请自行准备对应版本的游戏。

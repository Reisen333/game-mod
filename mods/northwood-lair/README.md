# The Northwood Lair（初音奴隶家族）VR 模组 v1.2.3

为游戏添加 SteamVR 立体画面、头部追踪、单手操作，以及可切换角色的第一人称视角。由 UUVR Mono Legacy 和 Northwood VR Fixes 组成。

**[下载分享版 ZIP](https://github.com/Reisen333/game-mod/releases/download/northwood-lair-vrmod-2026-10-28/VRmod.zip)** · [Release 页面](https://github.com/Reisen333/game-mod/releases/tag/northwood-lair-vrmod-2026-10-28) · [完整使用说明](package/使用说明.txt)

Release 中的文件名为 **VRmod.zip**，即本页介绍的 **1.2.3 分享包**，约 25.5 MiB。仓库中保留同内容、同 SHA-256 的 [ZIP 副本](downloads/Northwood_Lair_VR_Mod_v1.2.3.zip)。

## 安装

1. 关闭游戏，建议先复制一份游戏作为 VR 专用副本；已有其他模组时使用干净副本。
2. 将分享版 ZIP 的全部内容解压到游戏目录。解压后，以下文件必须与 **The Northwood Lair.exe 在同一层**：

   ~~~text
   游戏目录/
   ├─ The Northwood Lair.exe          ← 使用者原有的游戏文件
   ├─ The Northwood Lair_Data/        ← 使用者原有的游戏目录
   ├─ BepInEx/
   ├─ winhttp.dll
   ├─ doorstop_config.ini
   ├─ Start-VR.cmd
   ├─ 使用说明.txt
   └─ _VRModDocs/
   ~~~

3. 连接头显，在 PICO Connect 中进入电脑串流，再启动 SteamVR。
4. 双击 **Start-VR.cmd**，保持游戏窗口正常显示。首次启动会初始化 VR 组件。

不要多套一层目录。包内已带加载器、UUVR、设置管理器和当前插件，无需另装 Rai Pal、Python 或开发工具。

## 适配版本

- 游戏：**The Northwood Lair ver. 1.36**。
- 引擎：**Unity 2018.3.12f1，Mono，Windows x64**。
- 已测试设备链路：**PICO 4 + PICO Connect + SteamVR**。
- 当前插件：**Northwood VR Fixes 1.2.3**。

其他游戏版本、运行方式或头显尚未验证。默认使用 2560×1440 无边框全屏；F5 设置面板可调整桌面分辨率和窗口模式，VR 菜单尺寸单独控制。

## 手柄与键盘操作

左右手柄映射一致，任意一只即可完成操作。

| 操作 | 功能 |
| --- | --- |
| 手柄指向菜单 | 移动光标 |
| 扳机 | 鼠标左键确认；按住并明显移动可拖动菜单 |
| 摇杆上下 / 左右 | 摄像机升降 / 横移 |
| Y/B / X/A | 摄像机前移 / 后移 |
| 按住抓握并转动手柄 | 转动观察视角，上下默认反转 |
| 按下任意摇杆 | 打开 / 关闭角色视角面板 |
| F6 | 重置视角；绑定时重置眼点和头显中立位置 |
| F8 | 鼠标菜单点击 / 鼠标观察切换 |
| F5 | 打开模组设置 |
| Esc | 打开退出确认；再次按则取消 |

返回游戏菜单时，请指向游戏原有的返回按钮并扣扳机。手柄没有单独的返回键映射。

## 角色第一人称

默认进入自由视角。进入三维动画场景后，按下摇杆打开面板，用手柄指向、扳机选择角色。需要恢复自由移动时，在面板选择“自由视角”。

男性、男性怪物视点优先排列，常见代称如下：

| 代称 | 角色 |
| --- | --- |
| Man / Male | 男性角色 |
| Player | 玩家男性角色 |
| Henchman | 男性手下角色 |
| Monster | 男性怪物 |
| Skull Lord / SkL | 骷髅领主 |

Miku、Luka、Rin 等是女性角色。动画名称可能同时包含男女角色，应看面板前面的骨架身份标签；不能确认身份的条目会标注“待确认”。

绑定后，视点随角色移动，身体水平转向带动观察方向，头显仍可自由观察。原位移键改为眼点微调：默认每秒 5 厘米，各轴限制为正负 25 厘米。F6 可重置眼点，并记录当前头显中立位置。

角色校准保存在 BepInEx/config/NorthwoodVrFixes.actors.json。重新启动时仍默认自由视角，重新选择角色后读取自己的校准。场景切换或原角色失效后，需要重新选择。

## 已知问题与恢复

- 没有完整眼骨的模型使用头骨估算，可能需要手动微调眼点。
- 无法安全裁剪的头部、头发等网格会保留并提示适配限制。
- 用户已确认本轮测试场景中的人物识别、男性和怪物视点可用；尚未逐一验证全部动画、菜单、其他设备或专门的性能基准。
- 场景选择滚动菜单已加入点击防抖，保留明显移动时的拖动，仍应按实际菜单反馈排查。
- 模组首次启动会修改引擎 VR 设置并部署原生 VR 组件。**恢复原版请使用安装前的干净游戏副本**，仅删除插件文件不等于完整恢复原版。

问题日志位于游戏目录的 Northwood-VR.log 和 BepInEx/LogOutput.log。没有 VR 画面时，先确认电脑串流和 SteamVR 正常，再重新启动游戏。

## 文件说明

- downloads/：分享版 ZIP 和 SHA-256 校验文件。
- package/：与 ZIP 字节一致的展开内容，便于查看配置、使用说明和源码。
- [发布介绍](发布介绍.txt)：分享或发帖时可复制使用的简短介绍。
- package/_VRModDocs/：组件来源、许可证和源码，分享时请一并保留。

本包不含游戏本体、存档、个人校准、历史日志或开发工具，请自行准备对应版本的游戏。

ZIP SHA-256：

~~~text
0512731837282cfd27127c264ed55d0933eeefbbfd39f08f5cb08b76162627e4
~~~

# War3FixFont 后台版 v1.7.2

魔兽争霸 3 叠字问题修复工具。双击启动后直接在系统托盘运行，默认立即尝试修复，之后每 60 秒自动修复。

本仓库基于 [Zonciu/War3FixFont](https://github.com/Zonciu/War3FixFont) v1.7.1 修改，保留原作者版权与 [MIT 许可证](LICENSE)。修复原理沿用上游：通过 WinAPI 调整游戏窗口尺寸刷新画面。

## 下载与使用

[下载 v1.7.2 完整压缩包](https://github.com/a1144940203/War3FixFont/releases/download/v1.7.2/War3FixFont-v1.7.2-background.zip) · [查看发布页](https://github.com/a1144940203/War3FixFont/releases/tag/v1.7.2)

解压完整压缩包后双击 `War3FixFont.exe`。请保留同目录的 `War3FixFont.exe.config` 与 `LICENSE`。

运行需要 Windows 和 .NET Framework 4.8，沿用原项目的管理员权限要求。Windows 可能先显示 UAC 确认，确认后进入后台。先开工具或先开游戏均可；游戏未启动时，工具会等待。

| 操作 | 方法 |
| --- | --- |
| 显示设置 | 左键点击托盘图标、右键选择“显示”，或按默认 `Ctrl + Q` |
| 手动修复 | 设置窗口点击“修复一次”，或按默认 `Shift + Alt + D` |
| 暂停或恢复自动修复 | 取消或勾选“定时修复” |
| 返回后台 | 关闭设置窗口或最小化 |
| 退出程序 | 右键托盘图标，选择“退出” |

托盘图标可能位于 Windows 的“显示隐藏的图标”区域。再次双击程序会保留已有实例，并直接结束新实例。

定时开关、间隔、快捷键和窗口选项保存在程序旁的 `War3FixFont.json`，首次运行时自动生成。间隔支持 1–86400 秒；主动暂停后，下次启动仍保持暂停。本版不自动设置开机启动。

## v1.7.2 更新

- 启动后隐藏主窗口，不占任务栏；关闭或最小化设置窗口后继续运行。
- 修复原版启动时“定时修复”已勾选但定时器没有真正开启的问题。
- 重复启动直接结束新实例；快捷键冲突改用托盘通知提示。
- 扩大定时间隔范围，并将配置位置固定为程序目录。
- 定时修复在界面消息循环中执行；退出时停止并释放定时器、托盘图标和窗口钩子等资源。

详细记录见 [CHANGELOG.md](CHANGELOG.md)，离线使用说明见 [后台版 v1.7.2 说明](docs/后台版-v1.7.2.txt)。

## 编译与验证

在 Windows 上安装 Visual Studio 2022 Build Tools 的 .NET 桌面构建工具，在本仓库根目录运行：

```powershell
.\build.ps1
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File .\tests\BackgroundRegression.ps1 -ExecutablePath .\src\War3FixFont\bin\Release\War3FixFont.exe
```

构建脚本通过 PATH 或 `vswhere` 查找 MSBuild，通过 NuGet 获取 .NET Framework 4.8 引用程序集。默认生成 `src/War3FixFont/bin/Release/War3FixFont.exe`。

已完成 Release 构建和 **53 项后台回归检查**，结果见 [检查记录](tests/BackgroundRegression.results.txt)。检查覆盖隐藏启动、真实消息循环中的定时触发、托盘操作、配置保存与恢复、不同工作目录、重复实例与退出清理。检查使用隔离配置，未启动游戏；游戏中的实际叠字改善效果仍需观察验证。

原 Costura 构建依赖仍会产生 `System.Runtime` / `System.Reflection` 的版本冲突警告，未阻止本次构建和后台检查。

## 原有功能

- 手动、定时和快捷键修复。
- 无边框全屏、有边框最大化、无边框 4:3、自定义窗口。

## 来源与许可

上游：[Zonciu/War3FixFont](https://github.com/Zonciu/War3FixFont)，基础提交 `596bd5d139b2c94a369e8ccead97c26965d1f2a2`。本分支版本为 1.7.2，采用 MIT 许可证，保留原作者 Zonciu Liang 的版权声明。分发时请保留 [LICENSE](LICENSE)。

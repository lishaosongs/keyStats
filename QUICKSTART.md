# KeyStats 快速开始指南

## 安装

1. 使用 Scoop 安装：

   ```bash
   scoop bucket add keystats https://github.com/debugtheworldbot/scoop-keystats
   scoop install keystats
   ```

   或从 [GitHub Releases](https://github.com/debugtheworldbot/keyStats/releases) 下载 `KeyStats-Windows-*.zip`，解压后运行 `KeyStats.exe`。

2. 首次启动时 Windows 可能弹出安全警告，点击"仍要运行"。

Windows 版本**无需任何权限设置**，启动后会自动开始统计。

## 使用应用

### 托盘图标

运行后，应用会常驻系统托盘。左键点击托盘图标打开统计弹窗，右键打开菜单。

### 查看详细统计

统计弹窗显示：

- 键盘敲击次数
- 左键 / 右键点击次数
- 鼠标移动距离
- 滚动距离
- 最常按的按键分布
- 周/月历史图表（折线图或柱状图）

### 设置

在设置窗口可以：

- 开启 / 关闭托盘显示
- 设置通知阈值
- 设置开机自启
- 管理多设备同步（创建、配对、恢复、手动同步）

### 重置或退出

- 在弹窗中点击"重置统计"清零今日数据
- 右键托盘图标选择"退出"关闭 KeyStats

## 数据存储

应用数据存储在 `%LOCALAPPDATA%\KeyStats\`：

- `daily_stats.json` - 当日统计数据
- `history.json` - 历史数据
- `settings.json` - 用户设置

## 常见问题

### Q: 应用无法启动？

A: 确保你的 Windows 版本是 10 (1903+) 或 11。如果版本较旧，请升级系统或手动安装 [.NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework/net48)。

### Q: 统计数据丢失了？

A: 数据存储在 `%LOCALAPPDATA%\KeyStats\`。如果数据丢失，检查该目录下的文件是否存在。

### Q: 如何开机自启？

A: 在设置窗口中开启"开机自启"，应用会写入注册表 `HKCU\...\Run`。

### Q: 如何完全卸载？

A: 删除程序文件夹即可。如需清除数据，再手动删除 `%LOCALAPPDATA%\KeyStats\` 目录。

## 从源码构建

```bash
cd KeyStats.Windows
dotnet build KeyStats/KeyStats.csproj -c Release
dotnet test KeyStats.Sync.Tests/KeyStats.Sync.Tests.csproj
```

打包发布（输出 `dist/KeyStats-Windows-<版本>.zip`）：

```powershell
powershell -ExecutionPolicy Bypass -File ./build.ps1 -Configuration Release
```

## 技术支持

如有问题，请查看 README.md 或提交 Issue。

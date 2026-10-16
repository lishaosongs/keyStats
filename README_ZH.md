[English](./README.md) | 简体中文

# KeyStats - Windows 键鼠统计托盘应用

KeyStats 可以统计用户每日的键盘敲击次数、鼠标点击次数、鼠标移动距离和滚动距离，并支持可选的端到端加密多设备数据同步。

## 安装与使用

### 方式一：使用 Scoop 安装

```bash
scoop bucket add keystats https://github.com/debugtheworldbot/scoop-keystats
scoop install keystats
```

### 方式二：[从 GitHub Release 下载](https://github.com/debugtheworldbot/keyStats/releases) Windows 版本安装包

> **无需安装任何依赖**：Windows 版本使用 .NET Framework 4.8，Windows 10 (1903+) 和 Windows 11 已预装，开箱即用。如果你的 Windows 10 版本较旧（早于 1903），可以升级系统或[手动安装 .NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework/net48)。

### Linux（第三方移植版）

[下载 Linux 版本](https://github.com/0x5c0f/keyStats/releases)

## 功能特性

- **键盘敲击统计**：实时统计每日键盘按键次数
- **鼠标点击统计**：分别统计左键和右键点击次数
- **鼠标移动距离**：追踪鼠标移动的总距离
- **滚动距离统计**：记录页面滚动的累计距离
- **系统托盘集成**：常驻系统托盘，点击图标即可查看统计
- **详细面板**：点击托盘图标查看完整统计信息
- **按键分布与历史图表**：最常按的按键，以及周/月折线图与柱状图
- **每日自动重置**：午夜自动重置统计数据
- **数据持久化**：应用重启后数据不丢失
- **多设备数据同步**：在已配对设备之间端到端加密同步支持的每日键盘和鼠标点击统计

## 系统要求

- **Windows 10 (1903+) 或 Windows 11**
- **无需安装任何依赖**：使用 .NET Framework 4.8（Windows 10/11 已预装，开箱即用）
- **应用大小**：约 5-10 MB（轻量级，无需额外运行时）

> **注意**：如果你的 Windows 10 版本较旧（早于 1903），可以：
> 1. 升级到 Windows 10 1903 或更高版本（推荐）
> 2. 或手动安装 .NET Framework 4.8：[下载链接](https://dotnet.microsoft.com/download/dotnet-framework/net48)

## 首次运行

Windows 版本**无需额外权限设置**，应用启动后会自动开始统计。

> **注意**：首次启动时，Windows 可能会弹出安全警告，点击"仍要运行"即可。

## 隐私说明

KeyStats 仅保存聚合统计，**不会记录**：
- 输入的文字内容或按键顺序
- 鼠标光标或点击的具体位置

默认情况下，数据只保存在本机。只有用户主动开启多设备同步后，支持的每日按键总次数、各按键/组合键累计次数、鼠标按键点击次数和加密设备信息才会在本机加密后上传。目前不会同步鼠标移动距离、滚动距离和分应用统计，服务端也无法读取加密后的统计数据。

## 开发

### 构建与测试

```bash
cd KeyStats.Windows

# Debug / Release 构建
dotnet build KeyStats/KeyStats.csproj -c Release

# 自动化测试（同步协议、加密向量、调度、持久化）
dotnet test KeyStats.Sync.Tests/KeyStats.Sync.Tests.csproj

# 打包发布（输出 dist/KeyStats-Windows-<版本>.zip）
powershell -ExecutionPolicy Bypass -File ./build.ps1 -Configuration Release
```

架构说明见 [`KeyStats.Windows/CLAUDE.md`](./KeyStats.Windows/CLAUDE.md)，同步服务端见 [`services/sync-worker/README.md`](./services/sync-worker/README.md)。

### 目录结构

```
KeyStats.Windows/       Windows 托盘应用（WPF，.NET Framework 4.8）及同步测试
contracts/sync/v1/      E2EE 同步协议（JSON Schema、OpenAPI、fixtures）
services/sync-worker/   E2EE 同步的 Cloudflare Worker 服务端
scripts/                构建与发布脚本
docs/                   设计文档与实施计划
```

## 许可证

MIT License

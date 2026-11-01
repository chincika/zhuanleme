# 赚了么

一款 Windows 桌面小工具：输入每月到手工资和想要计入的公积金，在任务栏或悬浮窗中查看今天已经赚了多少。金额根据当前时间重新计算，每秒刷新。

## 功能

- **两种计算方式**：工作日模式按设定的工作时段和午休时间累加；全天模式按自然日均分，每天 24 小时累加。
- **工作日日历**：识别中国大陆法定节假日和调休上班日；支持按当月实际工作日或固定计薪天数计算。
- **收入显示**：只显示工资、分别显示工资和公积金，或合并显示；可选择 2～4 位小数。
- **任务栏小窗**：透明背景、置顶、拖动并保存位置，可选鼠标穿透。目前适配主屏幕横向任务栏。
- **悬浮窗**：可拖动，置顶与鼠标穿透可分别设置。

关闭穿透时，左键拖动任务栏数字，单击打开设置，右键打开菜单。开启任务栏穿透后，在数字区域按住鼠标右键约 0.7 秒即可打开菜单。穿透也可以通过系统托盘菜单关闭；托盘图标可能位于隐藏图标区域。

金额是按设定推算的估算值，不代表实际到账。单位特殊排班、请假、加班和跨夜排班不会自动识别。

## 下载和使用

从 [Releases](https://github.com/chincika/zhuanleme/releases) 下载 Windows x64 单文件版本，双击运行即可。旧版运行时请先通过托盘菜单退出，再打开新版；原有设置会自动沿用。

在设置中填写每月到手工资和公积金金额，选择计算方式、工作时间和显示方式。任务栏和悬浮窗可以单独或同时启用。程序不会自行设置开机启动。

## 本地数据与日历

工资、公积金、显示设置及节假日缓存保存在 `%LOCALAPPDATA%\ZhuanLeMe`；`settings.json` 是本地明文文件。程序内置 2026 年节假日数据，运行时会从 [holiday-cn](https://github.com/NateScarlet/holiday-cn) 及其 CDN 尝试更新当年和次年的公开数据；请求不包含工资、公积金或设置内容。未能获取当年数据时，工作日模式暂按周一至周五估算并提示。

## 从源码构建

需要 Windows、.NET 10 SDK 和 Windows Desktop 构建组件。在仓库目录执行：

```powershell
dotnet run --project tests/ZhuanLeMe.Tests.csproj -c Release
dotnet publish src/ZhuanLeMe.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

可执行文件位于 `publish/ZhuanLeMe.exe`。项目的计算和配置测试使用独立的临时数据，不读取个人工资设置。

### 交互测试

`ui-tests` 包含可选的模拟任务栏测试：

```powershell
dotnet run --project ui-tests/OverlaySmoke.csproj -c Release
```

该测试需要交互式 Windows 桌面，会短暂显示模拟窗口并移动鼠标，测试结束后恢复鼠标位置；它不在真实任务栏创建测试窗口，也不读取个人工资数据。普通构建和 CI 不会自动运行此测试。

## 技术与许可

使用 C#、.NET 10、WPF 和 Windows 原生分层窗口。项目以 [MIT License](LICENSE) 发布；第三方数据和参考代码的版权与许可见 [THIRD-PARTY-NOTICES.txt](src/THIRD-PARTY-NOTICES.txt)。

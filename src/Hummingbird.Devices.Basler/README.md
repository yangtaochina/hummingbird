# Hummingbird.Devices.Basler —— pylon 相机真机对接

本工程对接 Basler 相机真机。**Basler 官方不上 NuGet**（NuGet 上的 `Basler.Pylon` 是第三方冒名包，勿用），
必须安装官方 pylon SDK 后才能启用。

## 当前状态（2026-10-29）：已启用 ✅

- 本机已安装 **pylon 7.5.0.15658**，路径 `E:\Program Files\Basler\pylon 7`
- csproj 自动探测 `Development\Assemblies\Basler.Pylon\x64\Basler.Pylon.dll`，
  找到后定义 `HAS_PYLON` 并参与编译；已加入解决方案，App 的 Real 模式自动使用 `BaslerCameraService`
- API 已对照 SDK 文档核实（`Basler.Pylon.xml`）：
  - 单帧采集 = `StreamGrabber.GrabOne(timeoutMs)`（**pylon 7.5 无 Camera.OneShot**）
  - 像素格式枚举 = `PLCamera.PixelFormatEnum.Mono8`（不是 PLPixelFormat）
  - 采集模式 = `CameraOpened += Configuration.AcquireSingleFrame`

## 换电脑/换 SDK 路径

csproj 按以下顺序自动探测（命令行 `-p:PylonDll=...` 优先）：

1. `E:\Program Files\Basler\pylon 7\Development\Assemblies\Basler.Pylon\x64\Basler.Pylon.dll`
2. `C:\Program Files\Basler\pylon 7\Development\Assemblies\Basler.Pylon\x64\Basler.Pylon.dll`

都没有时本工程退化为空壳（`#if HAS_PYLON` 保护），解决方案照常编译，App 用 Sim 模式。

## 首次联机步骤

1. 打开 pylon Viewer（开始菜单 → Basler → pylon Viewer），确认相机可见、能出 Mono8 图
2. 本软件 `config.json` 里 `DeviceMode` 改为 `"Real"`；多台相机时填 `CameraSerialNumber`
3. 界面"设备调试"页 → 连接设备 → 拍照，日志区确认 `[相机] 采图成功`
4. 若报"非 Mono8 数据"，检查 pylon Viewer 里是否改过像素格式

## 参考例程

`E:\Program Files\Basler\pylon 7\Development\Samples\C#\Basler.Pylon\Grab\` —— 官方取图例程
（连续采集版；我们的场景是单帧，用 GrabOne 更简单）。

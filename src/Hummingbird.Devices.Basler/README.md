# Hummingbird.Devices.Basler —— 启用步骤

这个项目对接相机真机（Basler pylon）。**Basler 官方不上 NuGet**（NuGet 上的 Basler.Pylon 是第三方冒名包，勿用），
必须安装官方 pylon SDK 后才能编译：

1. 安装客户提供的 `Basler_pylon_7.5.0.15658.exe`（默认路径 `C:\Program Files\Basler\pylon 7`）。
   顺手装上附带的 `VC_redist.x64.exe`。
2. 安装完后，本项目的 csproj 会自动发现并引用 `Basler.Pylon.dll`（也支持自定义路径：`dotnet build -p:PylonDll=D:\pylon\bin\Basler.Pylon.dll`）。
3. 把本项目加入解决方案并让 App 引用：
   ```
   dotnet sln Hummingbird.sln add Hummingbird.Devices.Basler
   ```
   然后在 `Hummingbird.App.csproj` 增加对 `Hummingbird.Devices.Basler` 的 ProjectReference，
   并把 `MainForm` 里 Real 模式的相机构造替换为 `new BaslerCameraService(_cfg.CameraSerialNumber)`。
4. 首次联机建议先用 pylon Viewer 确认相机可见、能出图（mono8），再跑本软件。
5. 建议对照 SDK 的 C# 取图例程核对 `OneShot` / 参数名（不同 pylon 版本 API 细节略有出入）。

在完成上述步骤前，软件用 `Sim` 模式（模拟相机）开发调试，不受影响。

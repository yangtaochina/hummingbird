# 飞秒创芯

固定C表盘激光加工上位机，当前版本 **v1.7.0**。提供环境与参数、工位恢复、一键定位加工、同件补加工、光学与落点校准，以及分类加工资料与日志。

## 源码结构

- `src/`：WinForms程序、算法、设备接口、模拟设备、离线测试及孔位模板。
- `src/Hummingbird.App/机床程序/`：配套HM_MASTER、HM_RETURN、HM_OPTICAL，三套配置和操作说明。编译自动复制到输出目录。
- [飞秒创芯操作说明](飞秒创芯_操作说明.md)：日常操作与更新方式。
- [视觉兼容性说明](视觉兼容性优化_20261119.md)：1点／11点、圆环和椭圆参考孔的适用范围。
- `docs/操作/`：专项操作说明；`docs/开发/`：项目续接记录；`docs/归档/`：历史说明与旧输入模板。
- [验证记录](docs/验证记录/README.md)：保留的离线检查结果和历史原图回归摘要。

## 编译

使用Windows、.NET 10 SDK，Visual Studio打开`src/Hummingbird.slnx`，或在仓库根目录执行：

```powershell
dotnet restore src/Hummingbird.slnx
dotnet build src/Hummingbird.slnx -c Release --no-restore
```

主程序输出为`src/Hummingbird.App/bin/Release/net10.0-windows/`。现场相机需要安装Basler pylon x64 SDK；项目会按已有路径探测SDK，未安装时可用模拟模式检查界面。

本地`src/Hummingbird.App/config.json`不提交Git。首次可从`config.example.json`复制并填写现场连接参数；没有本地配置时，编译自动将无凭据、默认Sim模式的示例复制为输出中的`config.json`。更新现场程序时保留现场配置、操作偏好和加工资料，复制完整输出，不单独替换EXE。

## 离线检查

在仓库根目录执行，例如：

```powershell
dotnet run --project src/Hummingbird.SmokeTest -c Release --no-build -- --station-readiness
dotnet run --project src/Hummingbird.SmokeTest -c Release --no-build -- --automatic-optics
dotnet run --project src/Hummingbird.SmokeTest -c Release --no-build -- --face-boundary
dotnet run --project src/Hummingbird.SmokeTest -c Release --no-build -- --alignment
dotnet run --project src/Hummingbird.SmokeTest -c Release --no-build -- --elliptical-references output/ellipse-checks
```

这些入口使用模拟接口或合成图，不连接真实设备。部分历史原图回放需要本地`资料收集/`和`现场图片*/`原始资料；它们用于标定追溯、再加工和回归，保留原路径但不进入源码仓库。缺少现场资料时不能把对应历史回放报告成通过。

## 文件整理原则

可再生成的`bin/`、`obj/`、`.vs/`及重复解压交付包可清理；原始加工照片、trial、run、manifest和落点来源不按“旧文件”删除。旧交付目录只有与保留ZIP逐文件核对一致后才移除。机床配套文件统一以源码`机床程序/`目录为准。

现场连接配置和本地助手笔记不再跟踪。移除当前版本中的凭据不会改写既有Git历史；本次提交不重写历史。

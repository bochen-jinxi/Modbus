# Temperature3DAlarm — 基于 HelixToolkit 的 WPF 实时温度 3D 监测与报警

## 功能

- 使用 **HelixToolkit.Wpf** 在 WPF 中渲染 3D 场景，包含 5 个传感器节点（球体）。
- 后台 `Timer` 每 500ms 模拟一次温度数据，实时刷新 UI。
- 顶部大字号显示当前主监测点温度。
- **温度高于阈值 60°C 时**：
  - 对应 3D 球体颜色实时从冷色（蓝/绿）渐变为暖色（黄/橙/红）；
  - 顶部出现红色报警横幅；
  - 状态指示灯变红；
  - 右侧节点列表对应项标记"报警！"。
- 颜色随温度连续渐变，温度回落到阈值以下自动解除报警并恢复冷色。

## 颜色映射规则

| 温度区间 | 颜色 | 状态 |
|---|---|---|
| 20°C → 60°C | 蓝(#3399FF) → 绿(#33FF66) | 正常 |
| 60°C → 100°C | 黄(#FFE033) → 红(#FF2222) | 报警 |

阈值定义在 `TemperatureSensor.AlarmThreshold = 60.0`。

## 项目结构

```
Temperature3DAlarm/
├── Temperature3DAlarm.csproj   # .NET 8 WPF + HelixToolkit 2.24
├── App.xaml / App.xaml.cs      # 应用入口
├── MainWindow.xaml             # 主界面：3D 视图 + 状态栏 + 节点列表
├── MainWindow.xaml.cs          # 代码后置，绑定 ViewModel
├── MainViewModel.cs            # 实时温度模拟、报警状态聚合
├── TemperatureSensor.cs        # 单节点模型：温度→颜色计算
└── Converters.cs               # Color→Brush / Bool→Visibility / Sensor→ModelVisual3D
```

## 构建与运行

需要 .NET 8 SDK（Windows 桌面工作负载）。

```bash
cd f:/chenbo/Temperature3DAlarm
dotnet restore
dotnet build
dotnet run
```

## 关键实现点

- **3D 模型颜色实时变化**：`SensorToModelConverter` 为每个 `TemperatureSensor` 创建 `SphereVisual3D`，并订阅其 `PropertyChanged`。当 `ModelColor` 变化时立即替换 `sphere.Fill`，实现实时变色。
- **报警阈值判定**：`TemperatureSensor.UpdateAppearance()` 在每次温度更新时计算 `IsAlarming = Temperature > 60.0`，并据此选择冷色或暖色渐变区间。
- **UI 线程安全**：后台 `Timer` 回调通过 `Dispatcher.Invoke` 把所有属性更新切回 UI 线程，保证 WPF 数据绑定安全。

## 自定义

- 修改 `TemperatureSensor.AlarmThreshold` 调整报警阈值。
- 修改 `MainViewModel` 中的 `BaseTemp` / `SwingAmplitude` 调整温度模拟范围。
- 在 `Sensors` 集合中增删节点即可在 3D 场景中增删球体。

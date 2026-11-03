using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Temperature3DAlarm
{
    /// <summary>
    /// 单个传感器节点的温度数据模型。
    /// 负责根据当前温度计算 3D 模型应显示的颜色（正常/报警渐变）。
    /// </summary>
    public class TemperatureSensor : INotifyPropertyChanged
    {
        // 报警阈值（摄氏度）
        public const double AlarmThreshold = 60.0;

        private double _temperature;
        private Color _modelColor;
        private bool _isAlarming;

        public string Name { get; }

        /// <summary>
        /// 传感器在 3D 场景中的中心位置。
        /// </summary>
        public Point3D Position { get; }

        public TemperatureSensor(string name, Point3D position, double initialTemperature = 25.0)
        {
            Name = name;
            Position = position;
            _temperature = initialTemperature;
            UpdateAppearance();
        }

        /// <summary>
        /// 当前温度（摄氏度）。设置后会自动刷新颜色与报警状态。
        /// </summary>
        public double Temperature
        {
            get => _temperature;
            set
            {
                if (Math.Abs(_temperature - value) < 1e-6) return;
                _temperature = value;
                OnPropertyChanged();
                UpdateAppearance();
            }
        }

        /// <summary>
        /// 3D 模型当前颜色。温度越高越偏红，正常时为蓝/绿。
        /// </summary>
        public Color ModelColor
        {
            get => _modelColor;
            private set
            {
                if (_modelColor == value) return;
                _modelColor = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 是否处于报警状态（温度 > 阈值）。
        /// </summary>
        public bool IsAlarming
        {
            get => _isAlarming;
            private set
            {
                if (_isAlarming == value) return;
                _isAlarming = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 根据温度计算颜色：
        /// - 低于阈值：蓝(20°) -> 青 -> 绿(60°) 的冷色渐变
        /// - 高于阈值：黄(60°) -> 橙 -> 红(100°+) 的暖色渐变，并触发报警
        /// </summary>
        private void UpdateAppearance()
        {
            IsAlarming = _temperature > AlarmThreshold;

            if (!_isAlarming)
            {
                // 正常区间 [20, 60]：蓝 -> 绿
                double t = Clamp((_temperature - 20.0) / (AlarmThreshold - 20.0), 0.0, 1.0);
                ModelColor = Lerp(Color.FromRgb(0x33, 0x99, 0xFF), Color.FromRgb(0x33, 0xFF, 0x66), t);
            }
            else
            {
                // 报警区间 (60, 100]：黄 -> 红
                double t = Clamp((_temperature - AlarmThreshold) / (100.0 - AlarmThreshold), 0.0, 1.0);
                ModelColor = Lerp(Color.FromRgb(0xFF, 0xE0, 0x33), Color.FromRgb(0xFF, 0x22, 0x22), t);
            }
        }

        private static double Clamp(double v, double min, double max)
            => v < min ? min : (v > max ? max : v);

        private static Color Lerp(Color a, Color b, double t)
        {
            byte r = (byte)(a.R + (b.R - a.R) * t);
            byte g = (byte)(a.G + (b.G - a.G) * t);
            byte bl = (byte)(a.B + (b.B - a.B) * t);
            return Color.FromRgb(r, g, bl);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Media.Media3D;

namespace Temperature3DAlarm
{
    /// <summary>
    /// 主视图模型：管理多个传感器节点，并通过后台线程模拟实时温度变化。
    /// 温度高于 60 度时，对应 3D 模型颜色实时变红并触发报警。
    /// </summary>
    public class MainViewModel : INotifyPropertyChanged, IDisposable
    {
        public ObservableCollection<TemperatureSensor> Sensors { get; }

        private double _currentTemperature;
        private bool _isAlarming;
        private string _statusText;

        public MainViewModel()
        {
            Sensors = new ObservableCollection<TemperatureSensor>
            {
                // 在 3D 场景中布置 5 个传感器节点（一个设备的多点监测）
                new TemperatureSensor("主机-A", new Point3D(0, 0, 0), 28.0),
                new TemperatureSensor("主机-B", new Point3D(4, 0, 0), 30.0),
                new TemperatureSensor("散热-C", new Point3D(0, 3, 0), 35.0),
                new TemperatureSensor("电源-D", new Point3D(4, 3, 0), 45.0),
                new TemperatureSensor("核心-E", new Point3D(2, 1.5, 2), 40.0),
            };

            StatusText = "运行中";
            StartSimulation();
        }

        /// <summary>
        /// 当前主监测点温度（用于 UI 大字号显示）。
        /// </summary>
        public double CurrentTemperature
        {
            get => _currentTemperature;
            set
            {
                if (Math.Abs(_currentTemperature - value) < 1e-6) return;
                _currentTemperature = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 是否处于报警状态（任一节点超阈值）。
        /// </summary>
        public bool IsAlarming
        {
            get => _isAlarming;
            set
            {
                if (_isAlarming == value) return;
                _isAlarming = value;
                OnPropertyChanged();
            }
        }

        public string StatusText
        {
            get => _statusText;
            set
            {
                if (_statusText == value) return;
                _statusText = value;
                OnPropertyChanged();
            }
        }

        // 模拟控制
        private Timer _timer;
        private double _phase;
        private const double BaseTemp = 30.0;
        private const double SwingAmplitude = 40.0; // 让温度在 ~[-10, 70] 之间波动，会触发 60 度报警
        private bool _disposed;

        private void StartSimulation()
        {
            _timer = new Timer(OnTick, null, 500, 500);
        }

        private void OnTick(object state)
        {
            if (_disposed) return;
            try
            {
                // 用正弦波 + 少量噪声模拟温度起伏
                _phase += 0.08;
                double main = BaseTemp + Math.Sin(_phase) * SwingAmplitude + (Random.Shared.NextDouble() - 0.5) * 2.0;

                // 在 UI 线程上更新（WPF 数据绑定需要）
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    CurrentTemperature = main;

                    // 给每个节点一个略有差异的温度
                    for (int i = 0; i < Sensors.Count; i++)
                    {
                        double offset = Math.Sin(_phase + i * 0.7) * 6.0;
                        Sensors[i].Temperature = main + offset;
                    }

                    bool anyAlarm = false;
                    foreach (var s in Sensors)
                    {
                        if (s.IsAlarming) anyAlarm = true;
                    }
                    IsAlarming = anyAlarm;
                    StatusText = anyAlarm
                        ? $"报警：检测到温度超过 {TemperatureSensor.AlarmThreshold}°C"
                        : "运行中";
                });
            }
            catch (Exception ex)
            {
                App.Log("OnTick 异常: " + ex);
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public void Dispose()
        {
            _disposed = true;
            _timer?.Dispose();
        }
    }
}

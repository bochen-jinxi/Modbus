using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using HelixToolkit.Wpf;

namespace Temperature3DAlarm
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;

        public MainWindow()
        {
            try
            {
                _viewModel = new MainViewModel();
                DataContext = _viewModel;
                InitializeComponent();

                BuildScene();
                BuildSensorList();

                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
                Closing += (_, __) => _viewModel.Dispose();
            }
            catch (Exception ex)
            {
                App.Log("MainWindow 构造异常: " + ex);
                throw;
            }
        }

        /// <summary>
        /// 在 HelixViewport3D 中为每个传感器创建球体 + 文字标签，
        /// 并订阅属性变化实现实时变色与温度文本刷新。
        /// </summary>
        private void BuildScene()
        {
            foreach (var sensor in _viewModel.Sensors)
            {
                var sphere = new SphereVisual3D
                {
                    Center = sensor.Position,
                    Radius = 0.8,
                    Fill = new SolidColorBrush(sensor.ModelColor),
                };
                Viewport.Children.Add(sphere);

                var text = new TextVisual3D
                {
                    Position = new Point3D(sensor.Position.X, sensor.Position.Y, sensor.Position.Z + 1.2),
                    Text = $"{sensor.Name}\n{sensor.Temperature:F1}°C",
                    Height = 0.6,
                    Foreground = Brushes.White,
                };
                Viewport.Children.Add(text);

                sensor.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(TemperatureSensor.ModelColor))
                        sphere.Fill = new SolidColorBrush(sensor.ModelColor);
                    if (e.PropertyName == nameof(TemperatureSensor.Temperature))
                        text.Text = $"{sensor.Name}\n{sensor.Temperature:F1}°C";
                };
            }
        }

        /// <summary>
        /// 构建右侧节点列表。
        /// </summary>
        private void BuildSensorList()
        {
            var panel = new StackPanel();
            foreach (var sensor in _viewModel.Sensors)
            {
                var border = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 0, 0, 6),
                };

                var stack = new StackPanel();

                var header = new StackPanel { Orientation = Orientation.Horizontal };
                var dot = new Ellipse
                {
                    Width = 12, Height = 12, Margin = new Thickness(0, 0, 8, 0),
                    Fill = new SolidColorBrush(sensor.ModelColor),
                };
                var nameTb = new TextBlock
                {
                    Text = sensor.Name, Foreground = Brushes.White, FontWeight = FontWeights.Bold,
                    Width = 90, VerticalAlignment = VerticalAlignment.Center,
                };
                var tempTb = new TextBlock
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                UpdateTempText(tempTb, sensor.Temperature);
                header.Children.Add(dot);
                header.Children.Add(nameTb);
                header.Children.Add(tempTb);

                var statusTb = new TextBlock
                {
                    Margin = new Thickness(20, 2, 0, 0), FontSize = 11,
                };
                UpdateStatusText(statusTb, sensor.IsAlarming);

                stack.Children.Add(header);
                stack.Children.Add(statusTb);
                border.Child = stack;
                panel.Children.Add(border);

                sensor.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(TemperatureSensor.ModelColor))
                        dot.Fill = new SolidColorBrush(sensor.ModelColor);
                    if (e.PropertyName == nameof(TemperatureSensor.Temperature))
                        UpdateTempText(tempTb, sensor.Temperature);
                    if (e.PropertyName == nameof(TemperatureSensor.IsAlarming))
                        UpdateStatusText(statusTb, sensor.IsAlarming);
                };
            }
            SensorList.Items.Add(panel);
        }

        private static void UpdateTempText(TextBlock tb, double temp)
            => tb.Text = $"{temp:F1} °C";

        private static void UpdateStatusText(TextBlock tb, bool alarming)
        {
            if (alarming)
            {
                tb.Text = "报警！";
                tb.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55));
            }
            else
            {
                tb.Text = "正常";
                tb.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xCC, 0x66));
            }
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentTemperature))
            {
                TempText.Text = $"{_viewModel.CurrentTemperature:F1}°C";
            }
            if (e.PropertyName == nameof(MainViewModel.IsAlarming))
            {
                StatusDot.Color = _viewModel.IsAlarming
                    ? Color.FromRgb(0xFF, 0x22, 0x22)
                    : Color.FromRgb(0x33, 0xCC, 0x33);
                AlarmBanner.Visibility = _viewModel.IsAlarming
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            if (e.PropertyName == nameof(MainViewModel.StatusText))
            {
                StatusTextBlock.Text = _viewModel.StatusText;
            }
        }
    }
}

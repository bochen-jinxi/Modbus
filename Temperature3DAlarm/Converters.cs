using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;

namespace Temperature3DAlarm
{
    /// <summary>
    /// Color -> SolidColorBrush 转换器。
    /// </summary>
    public class ColorToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Color c) return new SolidColorBrush(c);
            return Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// bool -> Visibility 转换器。
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public static readonly BoolToVisibilityConverter Instance = new BoolToVisibilityConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// TemperatureSensor -> ModelVisual3D 转换器。
    /// 为每个传感器生成一个球体（颜色随温度变化）+ 文字标签。
    /// 当温度变化导致 ModelColor 改变时，通过监听 PropertyChanged 实时刷新材质。
    /// </summary>
    public class SensorToModelConverter : IValueConverter
    {
        public static readonly SensorToModelConverter Instance = new SensorToModelConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is TemperatureSensor sensor)
            {
                return BuildVisual(sensor);
            }
            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();

        private static ModelVisual3D BuildVisual(TemperatureSensor sensor)
        {
            var group = new ModelVisual3D();

            // 球体
            var sphere = new SphereVisual3D
            {
                Center = sensor.Position,
                Radius = 0.8,
                Fill = new SolidColorBrush(sensor.ModelColor),
            };
            group.Children.Add(sphere);

            // 文字标签（节点名 + 温度）
            var text = new TextVisual3D
            {
                Position = new Point3D(sensor.Position.X, sensor.Position.Y, sensor.Position.Z + 1.2),
                Text = $"{sensor.Name}\n{sensor.Temperature:F1}°C",
                Height = 0.6,
                Foreground = Brushes.White,
            };
            group.Children.Add(text);

            // 当传感器属性变化时，实时更新 3D 外观
            sensor.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(TemperatureSensor.ModelColor))
                {
                    sphere.Fill = new SolidColorBrush(sensor.ModelColor);
                }
                if (e.PropertyName == nameof(TemperatureSensor.Temperature))
                {
                    text.Text = $"{sensor.Name}\n{sensor.Temperature:F1}°C";
                }
            };

            return group;
        }
    }
}

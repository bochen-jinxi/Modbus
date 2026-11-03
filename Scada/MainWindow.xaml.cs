using System;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ChenboScada
{
    public partial class MainWindow : Window
    {
        readonly MainViewModel vm;

        public MainWindow()
        {
            InitializeComponent();
            // 组合根：工厂在这里注入，ViewModel 不直接 new 引擎
            vm = new MainViewModel((port, slaves) => new ModbusEngine(port, slaves));
            DataContext = vm;
            vm.Logs.CollectionChanged += (s, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add && LogBox.Items.Count > 0 && !vm.LogPaused)
                    LogBox.ScrollIntoView(LogBox.Items[LogBox.Items.Count - 1]);
            };
            Closing += (s, e) => vm.Dispose();
        }
    }

    // 日志分级着色：超时/错误=红，应答/发现=绿，发送=蓝，诊断/扫描=琥珀，原始字节=灰
    public class LogColorConverter : IValueConverter
    {
        static readonly Brush Red = Br(0xFF, 0x52, 0x52);
        static readonly Brush Green = Br(0x3D, 0xDC, 0x84);
        static readonly Brush Blue = Br(0x4F, 0xA8, 0xFF);
        static readonly Brush Amber = Br(0xFF, 0xB3, 0x00);
        static readonly Brush Gray = Br(0x6E, 0x76, 0x82);
        static readonly Brush Dflt = Br(0x9F, 0xB3, 0xD1);

        static Brush Br(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value as string ?? "";
            if (s.Contains("[超时") || s.Contains("[错误")) return Red;
            if (s.Contains("[应答") || s.Contains("发现从站")) return Green;
            if (s.Contains("[发送")) return Blue;
            if (s.Contains("[诊断") || s.Contains("[提示") || s.Contains("[扫描")) return Amber;
            if (s.Contains("[原始")) return Gray;
            return Dflt;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

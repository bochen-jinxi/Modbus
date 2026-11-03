using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace ChenboScada
{
    // 自绘双通道实时滚动波形：红=温度、蓝=湿度。
    // 数据点由引擎事件驱动（约7.5点/秒/通道），DispatcherTimer 10Hz 重绘实现滚动。
    public class WaveformControl : FrameworkElement
    {
        // 容量=240：900px 绘图宽下相邻点约 3.75px，Catmull-Rom 样条的平滑才肉眼可见；
        // 600 点时相邻点仅 1.5px，平滑曲线在视觉上退化成折线（平滑白做）。
        const int Cap = 240;
        readonly List<double> temps = new List<double>();
        readonly List<double> hums = new List<double>();
        readonly DispatcherTimer timer;

        static readonly Pen GridPen = new Pen(new SolidColorBrush(Color.FromRgb(40, 52, 74)), 1);
        static readonly Pen TempPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 90, 90)), 1.8);
        static readonly Pen HumPen = new Pen(new SolidColorBrush(Color.FromRgb(77, 178, 255)), 1.8);
        static readonly Brush BgBrush = new SolidColorBrush(Color.FromRgb(16, 24, 39));
        static readonly Brush EdgeBrush = new SolidColorBrush(Color.FromRgb(58, 74, 99));
        static readonly Brush TxtBrush = new SolidColorBrush(Color.FromRgb(159, 179, 209));

        public static readonly DependencyProperty ShowTempProperty =
            DependencyProperty.Register(nameof(ShowTemp), typeof(bool), typeof(WaveformControl),
                new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
        public bool ShowTemp { get => (bool)GetValue(ShowTempProperty); set => SetValue(ShowTempProperty, value); }

        public static readonly DependencyProperty ShowHumProperty =
            DependencyProperty.Register(nameof(ShowHum), typeof(bool), typeof(WaveformControl),
                new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
        public bool ShowHum { get => (bool)GetValue(ShowHumProperty); set => SetValue(ShowHumProperty, value); }

        public WaveformControl()
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (s, e) => InvalidateVisual();
            timer.Start();
        }

        public void AddPoint(double temp, double hum)
        {
            temps.Add(temp); hums.Add(hum);
            if (temps.Count > Cap) { temps.RemoveAt(0); hums.RemoveAt(0); }
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 50 || h < 50) return;
            dc.DrawRoundedRectangle(BgBrush, new Pen(EdgeBrush, 1.5), new Rect(0, 0, w, h), 10, 10);

            double left = 46, top = 14, pw = w - left - 12, ph = h - top - 30;
            Rect plot = new Rect(left, top, pw, ph);
            dc.PushClip(new RectangleGeometry(plot));

            for (int i = 0; i <= 4; i++)
            {
                double y = top + ph * i / 4;
                dc.DrawLine(GridPen, new Point(left, y), new Point(left + pw, y));
            }
            for (int i = 0; i <= 10; i++)
            {
                double x = left + pw * i / 10;
                dc.DrawLine(GridPen, new Point(x, top), new Point(x, top + ph));
            }

            if (temps.Count < 2 || (!ShowTemp && !ShowHum))
            {
                var hint = Txt(ShowTemp || ShowHum ? "等待数据…" : "请在下方勾选至少一个通道", 14);
                dc.DrawText(hint, new Point(left + pw / 2 - 40, top + ph / 2 - 10));
                dc.Pop();
                return;
            }

            double lo = double.MaxValue, hi = double.MinValue;
            if (ShowTemp) { lo = Math.Min(lo, temps.Min()); hi = Math.Max(hi, temps.Max()); }
            if (ShowHum) { lo = Math.Min(lo, hums.Min()); hi = Math.Max(hi, hums.Max()); }
            if (hi - lo < 1) { hi += 0.5; lo -= 0.5; }
            double pad = (hi - lo) * 0.15; hi += pad; lo -= pad;

            // X 按"实际点数"铺满全宽：点距 = pw/(n-1)，数据少时点距大(平滑肉眼可见)，
            // 攒满 Cap 后自然过渡到 3.75px/点的稳定滚动。若按 Cap 定死点距，
            // 数据没满时点挤在右侧，样条拐角只有1~2px——平滑白做。
            Point Map(int count, int i, double v) =>
                new Point(left + pw * i / Math.Max(count - 1, 1), top + ph * (hi - v) / (hi - lo));

            if (ShowTemp) DrawSeries(dc, temps, (i, v) => Map(temps.Count, i, v), TempPen);
            if (ShowHum) DrawSeries(dc, hums, (i, v) => Map(hums.Count, i, v), HumPen);
            dc.Pop();

            // Y 轴五档刻度
            for (int i = 0; i <= 4; i++)
            {
                double v = hi - (hi - lo) * i / 4;
                var ft0 = Txt(v.ToString("F1"), 10);
                dc.DrawText(ft0, new Point(left - 6 - ft0.Width, top + ph * i / 4 - 6));
            }
            if (ShowTemp)
            {
                var ft = Txt($"温度 {temps.Last():F1}℃", 13, TempPen.Brush);
                dc.DrawText(ft, new Point(w - 190, 16));
            }
            if (ShowHum)
            {
                var fh = Txt($"湿度 {hums.Last():F1}%RH", 13, HumPen.Brush);
                dc.DrawText(fh, new Point(w - 100, 16));
            }
            var fx = Txt($"最近 {temps.Count}/{Cap} 点 · 约 {temps.Count / 7.5:F0} 秒窗口（左旧右新）", 11);
            dc.DrawText(fx, new Point(left, h - 22));
        }

        void DrawSeries(DrawingContext dc, List<double> data, Func<int, double, Point> map, Pen pen)
        {
            var pts = new Point[data.Count];
            for (int i = 0; i < data.Count; i++) pts[i] = map(i, data[i]);
            if (pts.Length < 3)
            {
                for (int i = 1; i < pts.Length; i++) dc.DrawLine(pen, pts[i - 1], pts[i]);
                return;
            }
            // Catmull-Rom 过点样条 → 贝塞尔：控制点 C1=P1+(P2-P0)/6, C2=P2-(P3-P1)/6
            // 曲线严格穿过每个数据点（平滑但不改数据，峰谷不丢）
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(pts[0], false, false);
                for (int i = 0; i < pts.Length - 1; i++)
                {
                    Point p0 = pts[Math.Max(i - 1, 0)];
                    Point p1 = pts[i];
                    Point p2 = pts[i + 1];
                    Point p3 = pts[Math.Min(i + 2, pts.Length - 1)];
                    Point c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
                    Point c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
                    ctx.BezierTo(c1, c2, p2, true, false);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }

        static FormattedText Txt(string s, double size, Brush brush = null) =>
            new FormattedText(s, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Microsoft YaHei,Consolas"),
                size, brush ?? TxtBrush, 1.0);
    }
}

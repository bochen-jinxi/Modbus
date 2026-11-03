using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO.Ports;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace ChenboScada
{
    public class MainViewModel : INotifyPropertyChanged, IDisposable
    {
        readonly Func<string, byte[], ModbusEngine> engineFactory;   // 工厂由组合根注入，不在此 new
        readonly Dictionary<byte, int> timeoutTotal = new Dictionary<byte, int>();
        readonly Queue<string> pendingLogs = new Queue<string>();
        readonly DispatcherTimer secTimer;
        ModbusEngine engine;
        DateTime lastS1 = DateTime.MinValue, lastS2 = DateTime.MinValue;
        int lastRxTotal;

        public ObservableCollection<string> Ports { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> Logs { get; } = new ObservableCollection<string>();

        string selectedPort = "COM4";
        public string SelectedPort { get => selectedPort; set { selectedPort = value; PC(nameof(SelectedPort)); } }

        string slaveText = "01,02";
        public string SlaveText { get => slaveText; set { slaveText = value; PC(nameof(SlaveText)); } }

        bool isRunning;
        public bool IsRunning { get => isRunning; set { isRunning = value; PC(nameof(IsRunning)); PC(nameof(StartBtnText)); PC(nameof(CanEditPort)); } }
        public string StartBtnText => IsRunning ? "■ 停止" : "▶ 启动";
        public bool CanEditPort => !IsRunning;

        string statusText = "未连接";
        public string StatusText { get => statusText; set { statusText = value; PC(nameof(StatusText)); } }

        string s1Temp = "-- ℃", s1Hum = "-- %RH", s2Temp = "-- ℃", s2Hum = "-- %RH";
        public string S1Temp { get => s1Temp; set { s1Temp = value; PC(nameof(S1Temp)); } }
        public string S1Hum { get => s1Hum; set { s1Hum = value; PC(nameof(S1Hum)); } }
        public string S2Temp { get => s2Temp; set { s2Temp = value; PC(nameof(S2Temp)); } }
        public string S2Hum { get => s2Hum; set { s2Hum = value; PC(nameof(S2Hum)); } }

        string s1Updated = "--", s2Updated = "--";
        public string S1Updated { get => s1Updated; set { s1Updated = value; PC(nameof(S1Updated)); } }
        public string S2Updated { get => s2Updated; set { s2Updated = value; PC(nameof(S2Updated)); } }

        string s1Timeout = "0", s2Timeout = "0";
        public string S1Timeout { get => s1Timeout; set { s1Timeout = value; PC(nameof(S1Timeout)); } }
        public string S2Timeout { get => s2Timeout; set { s2Timeout = value; PC(nameof(S2Timeout)); } }

        bool s1Online, s2Online;
        public bool S1Online { get => s1Online; set { s1Online = value; PC(nameof(S1Online)); } }
        public bool S2Online { get => s2Online; set { s2Online = value; PC(nameof(S2Online)); } }

        bool s1Alarm, s2Alarm;
        public bool S1Alarm { get => s1Alarm; set { s1Alarm = value; PC(nameof(S1Alarm)); } }
        public bool S2Alarm { get => s2Alarm; set { s2Alarm = value; PC(nameof(S2Alarm)); } }

        static bool OutOfRange(double temp, double hum) =>
            temp < -40 || temp > 125 || hum < 0 || hum > 100;   // 温湿度传感器物理量程

        string statsText = "发送 0 · 应答 0 · 超时 0";
        public string StatsText { get => statsText; set { statsText = value; PC(nameof(StatsText)); } }

        string clockText = "";
        public string ClockText { get => clockText; set { clockText = value; PC(nameof(ClockText)); } }

        string frameText = "0.0 帧/秒";
        public string FrameText { get => frameText; set { frameText = value; PC(nameof(FrameText)); } }

        bool showRaw;
        public bool ShowRaw { get => showRaw; set { showRaw = value; PC(nameof(ShowRaw)); } }

        public ObservableCollection<SlaveChart> Charts { get; } = new ObservableCollection<SlaveChart>();
        readonly Dictionary<byte, SlaveChart> chartMap = new Dictionary<byte, SlaveChart>();

        void RebuildCharts()
        {
            Charts.Clear();
            chartMap.Clear();
            foreach (var id in ParseSlaves(SlaveText))
            {
                var c = new SlaveChart(id);
                Charts.Add(c);
                chartMap[id] = c;
            }
        }

        bool logPaused;
        public bool LogPaused
        {
            get => logPaused;
            set
            {
                logPaused = value; PC(nameof(LogPaused));
                if (!logPaused) while (pendingLogs.Count > 0) AppendLog(pendingLogs.Dequeue());
            }
        }

        public ICommand ToggleCommand { get; }
        public ICommand RefreshPortsCommand { get; }
        public ICommand ScanCommand { get; }
        public ICommand ClearLogsCommand { get; }
        public ICommand AboutCommand { get; }
        public ICommand ExitCommand { get; }

        public MainViewModel(Func<string, byte[], ModbusEngine> engineFactory)
        {
            this.engineFactory = engineFactory;
            ToggleCommand = new RelayCommand(_ => Toggle());
            RefreshPortsCommand = new RelayCommand(_ => RefreshPorts());
            ScanCommand = new RelayCommand(_ => StartScan());
            ClearLogsCommand = new RelayCommand(_ => Logs.Clear());
            AboutCommand = new RelayCommand(_ => MessageBox.Show(
                "尘博 SCADA 监控站 v1.0\nModbus RTU 温湿度采集 · 多从站轮询 · 总线扫描\n线程模型：发送/接收事件/帧超时检测 三执行流 + lock(io/gate) 临界区保护",
                "关于", MessageBoxButton.OK, MessageBoxImage.Information));
            ExitCommand = new RelayCommand(_ => Application.Current.Shutdown());
            secTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            secTimer.Tick += OnSecond;
            secTimer.Start();
            RefreshPorts();
            RebuildCharts();
        }

        void OnSecond(object s, EventArgs e)
        {
            ClockText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            int rx = engine != null ? rxTotal : 0;
            FrameText = $"{rx - lastRxTotal:F1} 帧/秒";
            lastRxTotal = rx;
            var now = DateTime.Now;
            S1Online = IsRunning && (now - lastS1).TotalSeconds < 3;
            S2Online = IsRunning && (now - lastS2).TotalSeconds < 3;
        }
        int rxTotal;

        public void RefreshPorts()
        {
            Ports.Clear();
            foreach (var p in SerialPort.GetPortNames()) Ports.Add(p);
            if (Ports.Count > 0 && !Ports.Contains(SelectedPort)) SelectedPort = Ports[0];
        }

        static byte[] ParseSlaves(string s)
        {
            try
            {
                var arr = s.Split(new[] { ',', '，', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                           .Select(x => byte.Parse(x.Trim(), System.Globalization.NumberStyles.HexNumber))
                           .ToArray();
                return arr.Length > 0 ? arr : new byte[] { 0x01 };
            }
            catch { return new byte[] { 0x01 }; }
        }

        void Toggle() { if (IsRunning) StopEngine(); else StartEngine(); }

        void StartEngine()
        {
            try
            {
                RebuildCharts();   // 波形图个数与轮询从站一致
                engine = engineFactory(SelectedPort, ParseSlaves(SlaveText));
                engine.ValuesUpdated += OnValues;
                engine.TimeoutOccurred += OnTimeout;
                engine.FrameSent += (id, f) => UI(() => AddLog($"[发送→从站{id:X2}] {Hex(f)}"));
                engine.FrameParsed += (id, f) => UI(() => AddLog($"[应答·从站{id:X2}] {Hex(f)}"));
                engine.Log += s => UI(() => AddLog(s));
                engine.RawReceived += (wait, data) => UI(() => { if (ShowRaw) AddLog($"[原始·等{wait:X2}时到] {Hex(data)}"); });
                engine.StatsChanged += (tx, rx, to) => UI(() => { rxTotal = rx; StatsText = $"发送 {tx} · 应答 {rx} · 超时 {to}"; });
                engine.Start();
                IsRunning = true;
                StatusText = $"已连接 {SelectedPort} @9600";
            }
            catch (Exception ex)
            {
                StatusText = "打开失败";
                AddLog("[错误] " + ex.Message);
            }
        }

        void StopEngine()
        {
            engine?.Stop();
            IsRunning = false;
            S1Online = S2Online = false;
            StatusText = "已断开";
        }

        void StartScan()
        {
            if (IsRunning) { AddLog("[提示] 请先停止轮询再扫描"); return; }
            if (engine != null && engine.Scanning) return;
            engine = engineFactory(SelectedPort, ParseSlaves(SlaveText));
            engine.Log += s => UI(() => AddLog(s));
            engine.Found += id => UI(() => AddLog($"[扫描] ★ 发现从站 0x{id:X2}"));
            engine.ScanProgress += (cur, total) => UI(() => StatusText = $"扫描中 0x{cur:X2} / {total} …");
            engine.ScanDone += () => UI(() => { StatusText = "扫描完成"; AddLog("[扫描] 结束，未列出的地址即无应答"); });
            engine.ScanAsync();
            StatusText = "扫描中…";
            AddLog("[扫描] 开始逐地址 01~F7 探测（每站等80ms，全程约20秒）…");
        }

        void OnTimeout(byte id)
        {
            UI(() =>
            {
                AddLog($"[超时] 从站{id:X2} 50ms 无应答");
                timeoutTotal.TryGetValue(id, out int n);
                n++;
                timeoutTotal[id] = n;
                if (id == 0x01) S1Timeout = n.ToString(); else if (id == 0x02) S2Timeout = n.ToString();
                if (n == 5)
                    AddLog($"[诊断] 从站{id:X2} 连续5次超时：可能不存在/未上电/地址不对，可从轮询列表移除；若日志有\"迟到应答\"则调大超时");
            });
        }

        void OnValues(byte slave, double temp, double hum)
        {
            UI(() =>
            {
                string now = DateTime.Now.ToString("HH:mm:ss");
                bool alarm = OutOfRange(temp, hum);
                if (slave == 0x01)
                {
                    S1Temp = $"{temp:F1} ℃"; S1Hum = $"{hum:F1} %RH"; S1Updated = now; lastS1 = DateTime.Now;
                    if (alarm != S1Alarm) { S1Alarm = alarm; if (alarm) AddLog($"[诊断] 从站01 数据越限（{temp:F1}℃/{hum:F1}%RH），超物理量程，请检查寄存器原始值"); }
                }
                else
                {
                    S2Temp = $"{temp:F1} ℃"; S2Hum = $"{hum:F1} %RH"; S2Updated = now; lastS2 = DateTime.Now;
                    if (alarm != S2Alarm) { S2Alarm = alarm; if (alarm) AddLog($"[诊断] 从站02 数据越限（{temp:F1}℃/{hum:F1}%RH），超物理量程，请检查寄存器原始值"); }
                }
                if (chartMap.TryGetValue(slave, out var chart)) chart.Wave.AddPoint(temp, hum);
            });
        }

        void AddLog(string s)
        {
            if (LogPaused) { pendingLogs.Enqueue(s); return; }
            AppendLog(s);
        }

        void AppendLog(string s)
        {
            Logs.Add($"{DateTime.Now:HH:mm:ss.fff}  {s}");
            while (Logs.Count > 500) Logs.RemoveAt(0);
        }

        static string Hex(byte[] b) => BitConverter.ToString(b);
        static void UI(Action a) => Application.Current?.Dispatcher.Invoke(a);

        public void Dispose() { secTimer.Stop(); StopEngine(); }

        public event PropertyChangedEventHandler PropertyChanged;
        void PC(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // 一个从站对应一个波形图：持有自己的 WaveformControl，数据按站分流
    public class SlaveChart : INotifyPropertyChanged
    {
        public string Title { get; }
        public WaveformControl Wave { get; } = new WaveformControl();
        bool showTemp = true, showHum = true;
        public bool ShowTemp { get => showTemp; set { showTemp = value; Wave.ShowTemp = value; PC(nameof(ShowTemp)); } }
        public bool ShowHum { get => showHum; set { showHum = value; Wave.ShowHum = value; PC(nameof(ShowHum)); } }
        public SlaveChart(byte id) { Title = $"SLAVE  0x{id:X2}"; }
        public event PropertyChangedEventHandler PropertyChanged;
        void PC(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class RelayCommand : ICommand
    {
        readonly Action<object> act;
        public RelayCommand(Action<object> act) { this.act = act; }
        public bool CanExecute(object p) => true;
        public void Execute(object p) => act(p);
        public event EventHandler CanExecuteChanged { add { } remove { } }
    }
}

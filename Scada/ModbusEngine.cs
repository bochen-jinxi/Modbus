using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading;

namespace ChenboScada
{
    // Modbus RTU 主站引擎：从控制台版 Program.cs 提炼，Console 输出改为事件，线程/锁协调原样保留。
    // 三执行流：SendLoop 发送线程、DataReceived 接收回调(池线程)、FrameGapLoop 帧超时检测线程。
    // 协调：lock(gate) 护 buf、lock(io) 护串口、AutoResetEvent 收发握手、volatile+Interlocked 保可见性。
    public class ModbusEngine
    {
        readonly List<byte> buf = new List<byte>();
        readonly object gate = new object();
        readonly object io = new object();
        readonly AutoResetEvent frameReceived = new AutoResetEvent(false);
        readonly string portName;
        readonly byte[] slaves;
        SerialPort sp;
        volatile bool running;
        volatile byte curSlave;
        long lastDataTicks;
        readonly Stopwatch sw = Stopwatch.StartNew();
        Thread tx, fx;

        const int BaudRate = 9600;
        const int RegCount = 2;
        const int TimeoutMs = 50;
        const int IntervalMs = 15;
        readonly double FrameGapMs;
        readonly double PollMs;

        int txCount, rxCount, timeoutCount;

        public event Action<byte, double, double> ValuesUpdated;   // 从站, 温度, 湿度
        public event Action<byte, byte[]> FrameSent;               // 从站, 请求帧
        public event Action<byte, byte[]> FrameParsed;             // 从站, 应答帧
        public event Action<byte> TimeoutOccurred;
        public event Action<string> Log;
        public event Action<int, int, int> StatsChanged;           // tx, rx, timeout
        public event Action<byte> Found;                           // 扫描发现从站
        public event Action<int, int> ScanProgress;                // 当前地址, 总数
        public event Action ScanDone;
        public event Action<byte, byte[]> RawReceived;             // 原始字节(此刻等待的从站, 数据)——诊断用

        public bool Scanning { get; private set; }

        // 总线扫描：逐地址发读请求，有应答即上报。复用现有接收链路(OnData/Drain/Handle/curSlave握手)。
        public void ScanAsync(int first = 1, int last = 247, int timeoutMs = 80)
        {
            if (Scanning || running) return;
            Scanning = true;
            new Thread(() =>
            {
                try
                {
                    sp = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One);
                    sp.DataReceived += OnData;
                    sp.Open();
                    sp.DiscardInBuffer();
                    for (int id = first; id <= last; id++)
                    {
                        byte[] req = { (byte)id, 0x03, 0x00, 0x00, (byte)(RegCount >> 8), (byte)(RegCount & 0xFF) };
                        byte[] frame = Crc(req);
                        curSlave = (byte)id;
                        lock (io) { sp.Write(frame, 0, frame.Length); }
                        if (frameReceived.WaitOne(timeoutMs)) Found?.Invoke((byte)id);
                        frameReceived.Reset();
                        lock (gate) { buf.Clear(); }
                        ScanProgress?.Invoke(id, last);
                    }
                    ScanDone?.Invoke();
                }
                catch (Exception ex) { Log?.Invoke("[扫描错误] " + ex.Message); ScanDone?.Invoke(); }
                finally
                {
                    lock (io) { if (sp != null && sp.IsOpen) sp.Close(); }
                    Scanning = false;
                }
            }) { IsBackground = true }.Start();
        }

        public ModbusEngine(string portName, byte[] slaves = null)
        {
            this.portName = portName;
            this.slaves = slaves ?? new byte[] { 0x01, 0x02 };
            FrameGapMs = 3.5 * 10.0 / BaudRate * 1000.0;
            PollMs = FrameGapMs / 2;
        }

        public bool IsRunning => running;

        public void Start()
        {
            if (running) return;
            sp = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One);
            sp.DataReceived += OnData;
            sp.Open();
            sp.DiscardInBuffer();
            running = true;
            tx = new Thread(SendLoop) { IsBackground = true };
            fx = new Thread(FrameGapLoop) { IsBackground = true };
            tx.Start();
            fx.Start();
            Log?.Invoke($"串口 {portName} 已打开（{BaudRate}bps），3.5字符={FrameGapMs:F3}ms");
        }

        public void Stop()
        {
            running = false;
            lock (io) { if (sp != null && sp.IsOpen) sp.Close(); }
            Log?.Invoke("已停止。");
        }

        void RaiseStats() => StatsChanged?.Invoke(txCount, rxCount, timeoutCount);

        void SendLoop()
        {
            int idx = 0;
            while (running)
            {
                byte id = slaves[idx];
                byte[] req = { id, 0x03, 0x00, 0x00, (byte)(RegCount >> 8), (byte)(RegCount & 0xFF) };
                byte[] frame = Crc(req);
                curSlave = id;
                lock (io)
                {
                    if (sp == null || !sp.IsOpen) break;
                    sp.Write(frame, 0, frame.Length);
                }
                Interlocked.Increment(ref txCount);
                FrameSent?.Invoke(id, frame);
                RaiseStats();

                if (!frameReceived.WaitOne(TimeoutMs))
                {
                    Interlocked.Increment(ref timeoutCount);
                    TimeoutOccurred?.Invoke(id);
                    RaiseStats();
                    lock (io) { if (sp != null && sp.IsOpen) sp.DiscardInBuffer(); }
                    lock (gate) { buf.Clear(); }
                    frameReceived.Reset();
                }
                idx = (idx + 1) % slaves.Length;
                Thread.Sleep(IntervalMs);   // 轮询间隔：必须用毫秒级 Sleep；SpinWait(15) 是迭代次数，不等于毫秒
            }
        }

        void OnData(object sender, SerialDataReceivedEventArgs e)
        {
            byte[] tmp;
            lock (io)
            {
                int n = sp.BytesToRead;
                if (n == 0) return;
                tmp = new byte[n];
                sp.Read(tmp, 0, n);
            }
            lock (gate) { buf.AddRange(tmp); }
            RawReceived?.Invoke(curSlave, tmp);
            Interlocked.Exchange(ref lastDataTicks, sw.ElapsedTicks);
            Drain();
        }

        void FrameGapLoop()
        {
            long handled = -1;
            while (running)
            {
                long last = Interlocked.Read(ref lastDataTicks);
                if (last != 0 && last != handled)
                {
                    double gapMs = (sw.ElapsedTicks - last) * 1000.0 / Stopwatch.Frequency; // 先乘后除，整数除法会恒为0
                    if (gapMs >= FrameGapMs)
                    {
                        handled = last;
                        OnFrameGap();
                    }
                }
                PreciseSleepMs(PollMs);
            }
        }

        void OnFrameGap()
        {
            Drain();
            byte[] rest = null;
            lock (gate)   // 锁内只放最短代码，严禁 Sleep
            {
                if (buf.Count > 0)
                {
                    rest = buf.ToArray();
                    buf.Clear();
                }
            }
            if (rest != null) Handle(rest);
        }

        static void PreciseSleepMs(double ms)
        {
            long target = (long)(ms / 1000.0 * Stopwatch.Frequency);
            long start = Stopwatch.GetTimestamp();
            while (Stopwatch.GetTimestamp() - start < target) Thread.SpinWait(20);
        }

        int FrameLength(List<byte> b, int start)
        {
            if (b.Count - start < 3) return -1;
            byte func = b[start + 1];
            if ((func & 0x80) != 0) return 5;
            if (func == 0x03 || func == 0x04)
            {
                int byteCount = b[start + 2];
                return 3 + byteCount + 2;
            }
            return -1;
        }

        void Drain()
        {
            while (true)
            {
                byte[] frame = null;
                lock (gate)
                {
                    while (buf.Count >= 3)
                    {
                        int len = FrameLength(buf, 0);
                        if (len < 0) return;
                        if (buf.Count < len) return;
                        frame = new byte[len];
                        buf.CopyTo(0, frame, 0, len);
                        if (CrcOk(frame))
                        {
                            buf.RemoveRange(0, len);
                            break;
                        }
                        Log?.Invoke("[对齐] CRC失败，滑动1字节");
                        buf.RemoveRange(0, 1);
                        frame = null;
                    }
                }
                if (frame == null) return;
                Handle(frame);
            }
        }

        void Handle(byte[] frame)
        {
            if (frame.Length < 4) return;
            ushort calc = Crc16(frame, frame.Length - 2);
            ushort recv = (ushort)(frame[frame.Length - 2] | (frame[frame.Length - 1] << 8));
            if (calc != recv) return;
            if (frame[0] != curSlave)
            {
                Log?.Invoke($"[忽略] 从站 {frame[0]:X2} 的迟到应答（当前等 {curSlave:X2}）");
                return;
            }
            byte func = frame[1];
            if ((func & 0x80) != 0)
            {
                Log?.Invoke($"[异常响应·从站{frame[0]:X2}] 功能码 0x{func:X2} 异常码 0x{frame[2]:X2}");
                frameReceived.Set();
                return;
            }
            if (func == 0x03 || func == 0x04)
            {
                int byteCount = frame[2];
                int n = byteCount / 2;
                if (n >= 2)
                {
                    ushort t = (ushort)((frame[3] << 8) | frame[4]);
                    ushort h = (ushort)((frame[5] << 8) | frame[6]);
                    Interlocked.Increment(ref rxCount);
                    FrameParsed?.Invoke(frame[0], frame);
                    ValuesUpdated?.Invoke(frame[0], t / 10.0, h / 10.0);
                    RaiseStats();
                }
                frameReceived.Set();
            }
        }

        static bool CrcOk(byte[] frame)
        {
            ushort calc = Crc16(frame, frame.Length - 2);
            ushort recv = (ushort)(frame[frame.Length - 2] | (frame[frame.Length - 1] << 8));
            return calc == recv;
        }

        static ushort Crc16(byte[] data, int len)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < len; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 0x0001) != 0) crc = (ushort)((crc >> 1) ^ 0xA001);
                    else crc >>= 1;
                }
            }
            return crc;
        }

        static byte[] Crc(byte[] data)
        {
            ushort crc = Crc16(data, data.Length);
            byte[] r = new byte[data.Length + 2];
            Array.Copy(data, r, data.Length);
            r[data.Length] = (byte)(crc & 0xFF);
            r[data.Length + 1] = (byte)(crc >> 8);
            return r;
        }
    }
}

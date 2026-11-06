using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading;

namespace ModbusDemo
{
    class Program
    {
        // 接收缓冲区：不管一次来几个字节，先按顺序堆到这里
        static readonly List<byte> buf = new List<byte>();
        static readonly object gate = new object();   // 保护 buf

        static readonly object io = new object();     // 保护串口读写（发送线程和接收线程都会用）

        static SerialPort sp;
        static volatile bool running = true;

        // ★★★ 周期轮询读的独立信号 + 活跃标记（彻底解决超时后迟到应答误 Set 全局信号问题）。
        //   原来用全局 frameReceived(AutoResetEvent) 给周期轮询读用，超时 Reset 后迟到应答可能再 Set，
        //   导致下一轮 WaitOne 误判。改用独立信号 pollAck + 活跃标记 pollActive：
        //   - SendLoop 每次发请求前置 pollActive=true
        //   - Handle 收到 0x03/0x04 周期轮询应答时，只有 pollActive==true 才 Set pollAck
        //   - 超时后置 pollActive=false，迟到应答到达时 pollActive 已 false → 不 Set
        //   ★ 不用 int 序号的正负判断（避免 int 溢出后正变负导致逻辑反转）。
        //   ★ 不再用全局 frameReceived（已删除）：它只有 Set 没有 WaitOne，是死代码。
        static readonly AutoResetEvent pollAck = new AutoResetEvent(false);
        static volatile bool pollActive = false;  // 是否有活跃的周期轮询请求（true=有，false=已超时/无）

        // ★ 写操作请求（每个按钮一次写就是一个 WriteRequest，自带独立应答信号，互不干扰）。
        //   支持多按钮同时操作：多个请求进队列，写线程串行执行，每个独立等自己的回显。
        class WriteRequest
        {
            public byte Slave;
            public ushort Addr;
            public bool On;
            public byte[] Frame;                 // 完整发送帧（含CRC），用于回显比对
            // Ack：Handle 收到 0x05 回显时 Set，ExecuteWrite 等它（每轮重试前 Reset）
            public readonly AutoResetEvent Ack = new AutoResetEvent(false);
            public bool AckOk;                   // 一次回显的结果（Ack Set 后由 ExecuteWrite 读）
            // Completed：WriteLoop 执行完整个请求（含重试）后 Set，WriteSingleCoil 等它
            public readonly ManualResetEventSlim Completed = new ManualResetEventSlim(false);
            public bool Result;                  // 最终结果（Completed Set 后由调用方读）
        }

        // ★ 写请求队列：CmdLoop / 多个按钮 / 远程命令 都往这里投递，写线程串行消费。
        //   ConcurrentQueue 线程安全，多线程并发 Enqueue 不会互相阻塞。
        static readonly ConcurrentQueue<WriteRequest> writeQueue = new ConcurrentQueue<WriteRequest>();

        // ★ 当前写线程正在等回显的请求（同一时刻只有一个，因为写线程串行执行）。
        //   接收线程 Handle 通过它把 0x05 回显分发给"正确的那个请求"，避免错配。
        //   volatile 保证接收线程能看到写线程的赋值。
        static volatile WriteRequest activeWrite;

        // ★ 读操作请求（开到位成功后采集设备应答数据：转速/电压/电流等）。
        //   与 WriteRequest 同构：每个读任务独立等自己的 0x03 应答，互不干扰。
        //   读任务由 ExecuteWrite 成功后投递到 readQueue，SendLoop 优先消费。
        class ReadRequest
        {
            public byte Slave;
            public ushort StartAddr;
            public ushort RegCount;
            public string Tag;                 // 用途标识："转速"/"电压"/"电流"
            public double Scale;               // 工程量换算系数（如 0.1 表示读出值*0.1）
            public string Unit;                // 单位："RPM"/"V"/"A"
            public byte[] Frame;               // 完整发送帧（含CRC），用于回显比对
            public readonly AutoResetEvent Ack = new AutoResetEvent(false);
            public bool AckOk;
            public ushort[] Values;            // 读到的寄存器值
            public readonly ManualResetEventSlim Completed = new ManualResetEventSlim(false);
            public bool Result;
        }

        // ★★★ 0x02 读离散输入验证请求（强闭环：写线圈成功后验证物理设备真的到位了）。
        //   与 ReadRequest 同构，但功能码是 0x02，应答按"位"解析。
        class VerifyRequest
        {
            public byte Slave;
            public ushort InputAddr;          // 离散输入地址
            public bool ExpectOn;             // 期望值（true=ON 算到位）
            public byte[] Frame;              // 完整发送帧（含CRC）
            public readonly AutoResetEvent Ack = new AutoResetEvent(false);
            public bool AckOk;
            public bool ActualOn;             // 读到的实际值
        }

        // ★ 当前正在等 0x02 应答的验证请求（同一时刻只有一个，busLock 保证）。
        static volatile VerifyRequest activeVerify;

        // ★ 读请求队列：ExecuteWrite 成功后投递，SendLoop 优先消费。
        //   事件触发的读优先于周期轮询的读（开到位后的数据更重要）。
        static readonly ConcurrentQueue<ReadRequest> readQueue = new ConcurrentQueue<ReadRequest>();

        // ★ 当前读线程正在等应答的请求（同一时刻只有一个，busLock 保证）。
        //   接收线程 Handle 通过它把 0x03 应答分发给"正确的那个读任务"。
        static volatile ReadRequest activeRead;

        // ★ 设备读配置：写线圈地址 → 开到位后要读哪些数据。
        //   key = (从站号, 线圈地址)，value = 该线圈开到位后要采集的数据描述列表。
        //   例如：电机启动线圈0 开到位后，读 转速@0x0100 + 电压@0x0102 + 电流@0x0104。
        //   DelayMs：开到位成功后延迟多少毫秒再读（等设备数据就绪，如电机转速上升）。
        class ReadProfile
        {
            public string Tag;
            public ushort StartAddr;
            public ushort RegCount;
            public double Scale;
            public string Unit;
            public int DelayMs;
        }

        // ★★★ 强闭环验证配置：写线圈成功后，读离散输入(0x02)验证物理设备真的到位了。
        //   0x05 回显只表示"从站收到了命令"，不表示"设备真的启动了"。
        //   读 0x02 离散输入（传感器信号，如"电机运行中"、"阀门到位"）才能确认物理状态。
        //   VerifyAddr：离散输入地址（0x02 功能码读）
        //   VerifyOnValue：期望值（true=ON，false=OFF）
        //   VerifyDelayMs：写成功后延迟多少毫秒再读验证（等设备物理响应）
        class VerifyProfile
        {
            public ushort VerifyAddr;       // 离散输入地址（0x02 功能码读）
            public bool VerifyOnValue;      // 期望值（true=ON 算启动成功）
            public int VerifyDelayMs;       // 写成功后延迟再读验证（等设备物理响应）
        }

        // ★ 读配置表：硬编码（简单直接，后续可扩展为 JSON 配置文件）。
        //   示例：从站1 线圈0（电机启动）开到位后，延迟 500ms 读转速/电压/电流。
        static readonly Dictionary<(byte slave, ushort coilAddr), ReadProfile[]> ReadProfiles =
            new Dictionary<(byte, ushort), ReadProfile[]>
            {
                { (0x01, 0), new[]
                    {
                        new ReadProfile { Tag = "转速", StartAddr = 0x0100, RegCount = 1, Scale = 1.0,   Unit = "RPM", DelayMs = 500 },
                        new ReadProfile { Tag = "电压", StartAddr = 0x0101, RegCount = 1, Scale = 0.1,   Unit = "V",   DelayMs = 500 },
                        new ReadProfile { Tag = "电流", StartAddr = 0x0102, RegCount = 1, Scale = 0.01,  Unit = "A",   DelayMs = 500 },
                    }
                },
            };

        // ★★★ 强闭环验证配置表：写线圈成功后读 0x02 验证物理状态。
        //   示例：从站1 线圈0（电机启动）写 ON 成功后，延迟 200ms 读离散输入地址 0x0000，
        //   期望值为 ON(true) 才算"真正开到位"。
        static readonly Dictionary<(byte slave, ushort coilAddr), VerifyProfile> VerifyProfiles =
            new Dictionary<(byte, ushort), VerifyProfile>
            {
                { (0x01, 0), new VerifyProfile { VerifyAddr = 0x0000, VerifyOnValue = true, VerifyDelayMs = 200 } },
            };

        // ★ 总线事务锁：RS485 半双工要求"整个事务（发+等应答）"独占总线，不能读写并发。
        //   SendLoop 用 TryEnter 非阻塞试，拿不到说明写事务在执行，跳过本轮读（写优先）。
        //   WriteLoop 用 lock 阻塞等，保证写命令一定能执行。
        //   锁顺序：busLock → io → gate，统一不死锁。
        static readonly object busLock = new object();

        // ★ 写命令失败重试次数（超时或异常响应都算失败，重发最多 WriteRetry 次）
        const int WriteRetry = 3;

        // ★ 多从站轮询：要轮询的从站地址列表（RS485总线上挂多个从站，同一时刻只有一个能应答）
        static readonly byte[] Slaves = { 0x01, 0x02 };

        // 当前正在等哪个从站的应答（发送线程写，接收线程读，volatile保证可见性）
        // ★ 用来校验"应答是谁的"——别的从站的迟到应答不算数，防止错配
        static volatile byte curSlave;

        // 波特率（用于算 3.5 字符时间）
        const int BaudRate = 9600;

        // 请求读取的寄存器个数 N（动态帧长的来源）
        const int RegCount = 2;

        // 同步模式：发请求后等待应答的超时（毫秒）。
        // 必须 > 一次交互时间（9600下≈18ms）+ 从站轮询延迟 + 系统调度抖动，
        // 实测30ms偏紧（超时18次），50ms稳（超时3次）。
        const int TimeoutMs = 50;

        // 两次请求之间的固定间隔（毫秒）
        // Windows 默认时钟节拍约 15.6ms，Thread.Sleep(15) 实际≈15.6ms（名实相符）。
        // 发送间隔不需要 3.65ms 级精度（那是帧定界用的），用 Sleep 不烧 CPU。
        const int IntervalMs = 15;

        // ★ 详细日志开关：false 时只打印温湿度结果/超时/异常/写命令，屏幕安静便于输入 on/off
        const bool Verbose = false;

        // ★ 高精度计时器：Stopwatch 基于 QueryPerformanceCounter，
        //   分辨率可达微秒级（远高于 System.Threading.Timer 的约15ms）
        static readonly Stopwatch sw = Stopwatch.StartNew();

        // 最后一次收到数据的时刻（Stopwatch 的 ticks）。
        // 用 Interlocked 读写，跨线程安全。0 表示还没收到过数据。
        static long lastDataTicks;

        // ★ 帧超时阈值 = 3.5 字符时间（按波特率动态算）。
        //   1字符=10位（起始+8数据+停止）→ 3.5*10/BaudRate 秒
        //     9600  → 3.65ms
        //     115200→ 0.304ms
        //   判定规则（Modbus规范）：
        //     帧内字符间隔 1.04ms（9600，1字符）→ 继续攒帧
        //     允许抖动：字节间隔慢到 1.5ms 仍 < 3.65ms → 仍算帧内，被自然容忍
        //     超过 3.5 字符（3.65ms）没新数据 → 判定帧传输结束
        //   也就是说：1.5ms~3.65ms 之间的"模糊区"不影响判定——
        //             只要 gap < 3.65ms 就继续攒，gap >= 3.65ms 才判定帧结束。
        static readonly double FrameGapMs = 3.5 * 10.0 / BaudRate * 1000.0;

        // 帧超时检测线程的轮询间隔（毫秒）。
        // 取帧超时阈值的一半（PollMs <= FrameGapMs/2），保证能及时发现帧结束。
        //   9600 → 3.65/2 ≈ 1.83ms；115200 → 0.304/2 ≈ 0.152ms
        //   检测延迟最多 PollMs（1.83ms）< 帧间静默（3.65ms）→ 不会漏判。
        static readonly double PollMs = FrameGapMs / 2;

        static void Main(string[] args)
        {
            // ★ 参数模式：dotnet run on 1 0  → 直接执行一次写命令后退出（不进交互轮询，便于脚本/测试）
            if (args.Length >= 3 && (args[0] == "on" || args[0] == "off")
                && byte.TryParse(args[1], out byte pSlave) && ushort.TryParse(args[2], out ushort pAddr))
            {
                sp = new SerialPort("COM4", BaudRate, Parity.None, 8, StopBits.One);
                sp.WriteTimeout = 1000;          // ★ 写超时1秒，避免驱动层无限阻塞
                sp.DataReceived += OnData;
                sp.Open();
                sp.DiscardInBuffer();
                Console.WriteLine($"[参数模式] 串口已打开，执行 {(args[0] == "on" ? "开到位" : "关到位")} 从站{pSlave} 线圈{pAddr}");
                Thread pFx = new Thread(FrameGapLoop) { IsBackground = true };
                pFx.Start();
                // ★ 写线程（串行消费写队列）
                Thread pWx = new Thread(WriteLoop) { IsBackground = true };
                pWx.Start();
                bool ok;
                try { ok = args[0] == "on" ? SwitchOn(pSlave, pAddr) : SwitchOff(pSlave, pAddr); }
                catch (Exception ex) { Console.WriteLine($"[参数模式] 执行异常：{ex.Message}"); ok = false; }
                Console.WriteLine(ok ? "[参数模式] 执行成功" : "[参数模式] 执行失败");
                running = false;
                lock (io) { if (sp.IsOpen) sp.Close(); }
                return;
            }

            sp = new SerialPort("COM4", BaudRate, Parity.None, 8, StopBits.One);
            sp.WriteTimeout = 1000;          // ★ 写超时1秒，避免驱动层无限阻塞
            sp.DataReceived += OnData;
            sp.Open();
            sp.DiscardInBuffer();   // 清掉打开时可能残留的旧数据
            Console.WriteLine($"串口已打开（{BaudRate}bps），3.5字符时间={FrameGapMs:F3}ms，轮询间隔={PollMs:F3}ms，开始读取...");

            Console.CancelKeyPress += (s, e) => { e.Cancel = true; running = false; };

            // 发送线程
            Thread tx = new Thread(SendLoop) { IsBackground = true };
            tx.Start();

            // ★ 帧超时检测线程（用 Stopwatch 高精度判定 3.5 字符静默）
            Thread fx = new Thread(FrameGapLoop) { IsBackground = true };
            fx.Start();

            // ★ 写线程：串行消费写队列，保证 RS485 总线一次只发一帧。
            //   多个按钮/命令并发投递到 writeQueue，这里排队执行，每个请求独立等自己的回显。
            Thread wx = new Thread(WriteLoop) { IsBackground = true };
            wx.Start();

            // ★ 命令线程：让用户交互输入 on/off 执行开关的开到位/关到位
            Thread cmd = new Thread(CmdLoop) { IsBackground = true };
            cmd.Start();

            while (running) Thread.Sleep(200);     // 主线程等待 Ctrl+C

            lock (io) { if (sp.IsOpen) sp.Close(); }
            Console.WriteLine("已退出。");
        }

        // 发送线程（同步模式，多从站轮询 + 事件触发读）：
        // ★ 优先消费 readQueue（开到位后的设备数据采集），没有才做周期轮询。
        //   事件触发的读比周期轮询更重要（开到位后的转速/电压/电流是用户主动要的）。
        // RS485是半双工总线，同一时刻只能有一个从站应答。
        // ★ 用 busLock 事务级互斥：写事务执行时 TryEnter 失败，跳过本轮读（写优先）。
        static void SendLoop()
        {
            int idx = 0;                                   // 当前轮到第几个从站
            while (running)
            {
                // ★ 非阻塞试 busLock，拿不到说明写事务在执行，跳过本轮读
                if (!Monitor.TryEnter(busLock, 0))
                {
                    // Windows 默认时钟节拍约 15.6ms，Sleep(1) 实际也睡 15.6ms，
                    // 显式写 15 名实相符。读轮询间隔本身 15ms，让步 15.6ms 无感。
                    Thread.Sleep(15);
                    continue;
                }
                try
                {
                    // ★ 优先消费 readQueue：开到位后的设备数据采集任务
                    if (readQueue.TryDequeue(out var rr))
                    {
                        ExecuteRead(rr);
                        Thread.Sleep(IntervalMs);   // 读任务间隔，避免连续轰炸总线
                        continue;                    // 继续下一轮（可能还有读任务排队）
                    }

                    byte id = Slaves[idx];
                    // 构造请求：从站地址id + 功能码03 + 起始地址0000 + 寄存器数N + CRC  Modbus RTU 的多字节字段也是"高字节先"（大端）：只有CRC 是小端（低字节先）
                    byte[] req = { id, 0x03, 0x00, 0x00, (byte)(RegCount >> 8), (byte)(RegCount & 0xFF) };
                    byte[] frame = Crc(req);
                    curSlave = id;                             // ★ 告诉接收线程：现在在等这个从站
                    // ★★★ 独立信号 + 活跃标记：发请求前标记活跃，Handle 只在活跃时 Set pollAck。
                    pollActive = true;
                    pollAck.Reset();                           // 清上一次可能残留的信号

                    try
                    {
                        if (!SendFrame(frame)) break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[发送异常·从站{id:X2}] {ex.Message}，跳过本轮");
                        idx = (idx + 1) % Slaves.Length;
                        continue;
                    }
                    if (Verbose) Console.WriteLine($"[发送→从站{id:X2}]{DateTime.Now:yyyy-MM-dd HH:mm:ss} {Hex(frame)}");

                    if (!pollAck.WaitOne(TimeoutMs))
                    {
                        if (Verbose) Console.WriteLine($"[超时] 从站{id:X2} {TimeoutMs}ms 没应答，清空接收状态后轮下一个");
                        // ★ 超时说明该从站的应答丢了/迟到。清空接收状态，防止迟到的旧应答
                        //   错配到下一个请求（否则请求-应答错位持续累积、buf堆积）。
                        //   锁顺序保持 io→gate（与OnData一致），不会死锁。
                        ClearRxState();   // 丢弃驱动缓冲旧数据 + 已攒残帧
                        // ★★★ 彻底修复：用活跃标记 pollActive，不依赖 int 序号（避免溢出）。
                        //   超时后置 pollActive=false，迟到应答检查 pollActive==false → 不 Set pollAck。
                        //   下一轮发请求前会置 pollActive=true，不影响正常流程。
                        pollActive = false;      // 失效活跃标记，迟到应答不 Set pollAck
                        curSlave = 0xFF;         // 失效从站号（双保险）
                        pollAck.Reset();
                    }

                    idx = (idx + 1) % Slaves.Length;
                }
                finally
                {
                    Monitor.Exit(busLock);
                }
                // ★ 轮到下一个从站（循环轮询）
                Thread.Sleep(IntervalMs);
            }
        }

        // ★ 清空接收状态：丢弃驱动缓冲里的旧数据 + 丢弃已攒的残帧。
        //   SendLoop 超时分支、ExecuteWrite/ExecuteRead 每次发前都调用，复用统一。
        //   锁顺序 io→gate（与 OnData 一致），不会死锁。
        //   ★ 不含 Ack.Reset()：SendLoop 没有 wr.Ack/rr.Ack，调用方各自 Reset 自己的 Ack。
        static void ClearRxState()
        {
            lock (io) { if (sp.IsOpen) sp.DiscardInBuffer(); }
            lock (gate) { buf.Clear(); }
        }

        // ★ 发送一帧到串口。统一 IsOpen 检查 + lock(io) 保护。
        //   返回 true=发送成功，false=串口已关闭。
        //   ★ 不吞异常：sp.Write 可能抛异常（写超时/串口故障），调用方 try-catch 处理。
        //   这样调用方可以保留自己的异常日志格式（发送异常/写异常/读异常）和重试逻辑。
        static bool SendFrame(byte[] frame)
        {
            lock (io)
            {
                if (!sp.IsOpen) return false;
                sp.Write(frame, 0, frame.Length);
            }
            return true;
        }

        // 步骤1：每收到一段就追加到缓冲区，并记录时刻
        static void OnData(object sender, SerialDataReceivedEventArgs e)
        {
            byte[] tmp;
            lock (io)
            {
                int n = sp.BytesToRead;
                if (n == 0) return;
                tmp = new byte[n];
                sp.Read(tmp, 0, n);
            }

            // ★ AddRange 和 DrainInLock 合并到同一个 lock(gate) 块，原子切帧。
            //   否则 buf.Clear 可在 AddRange 后、Drain 前插入，导致数据加了还没切帧就被清掉。
            int count;
            List<byte[]> frames;
            lock (gate)
            {
                buf.AddRange(tmp);
                count = buf.Count;
                frames = DrainInLock();
            }
            if (Verbose) Console.WriteLine($"    >>> 收到 {tmp.Length} 字节，缓冲区现有 {count} 字节");

            // ★ 高精度记录"最后收到数据的时刻"
            Interlocked.Exchange(ref lastDataTicks, sw.ElapsedTicks);

            foreach (var f in frames) Handle(f);
        }

        // ★ 帧超时检测线程：用高精度 Stopwatch 判断
        //   "距最后一次收到数据是否已超过 3.5 字符时间"
        static void FrameGapLoop()
        {
            long handled = -1;   // 已处理过的 lastDataTicks，避免同一批重复触发
            while (running)
            {
                long last = Interlocked.Read(ref lastDataTicks);
                if (last != 0 && last != handled)
                {
                    double gapMs = (sw.ElapsedTicks - last) * 1000.0 / Stopwatch.Frequency;
                    if (gapMs >= FrameGapMs)
                    {
                        handled = last;      // 标记这批已处理
                        OnFrameGap();
                    }
                }
                PreciseSleepMs(PollMs);      // 高精度短睡，保证轮询节拍
            }
        }

        // 帧超时到：先按帧长切走所有完整帧，剩余不足一帧的当残帧处理
        static void OnFrameGap()
        {
            List<byte[]> frames;
            byte[] rest = null;
            lock (gate)
            {
                frames = DrainInLock();
                if (buf.Count > 0)
                {
                    rest = buf.ToArray();
                    buf.Clear();
                }
            }
            foreach (var f in frames) Handle(f);
            if (rest != null) Handle(rest);
        }

        // ★ 高精度睡眠：用 Stopwatch 计时 + SpinWait 忙等，精度达微秒级
        //   （System.Threading.Timer / Thread.Sleep 精度只有约15ms，做不到3.5字符级）
        static void PreciseSleepMs(double ms)
        {
            long target = (long)(ms / 1000.0 * Stopwatch.Frequency);
            long start = sw.ElapsedTicks;
            while (sw.ElapsedTicks - start < target)
            {
                Thread.SpinWait(20);
            }

        }

        // 变长帧核心：算出缓冲区里从 start 开始这一帧的总长度。
        // 返回 -1 表示"还不能判断"（数据不够或功能码未知）。
        static int FrameLength(List<byte> b, int start)
        {
            if (b.Count - start < 3) return -1;        // 至少够帧头3字节

            byte func = b[start + 1];

            if ((func & 0x80) != 0) return 5;          // 异常响应：固定5字节

            if (func == 0x05) return 8;                // ★ 写单个线圈应答：固定8字节（请求的回显）

            if (func == 0x02 || func == 0x03 || func == 0x04)  // 读离散输入/读寄存器应答
            {
                int byteCount = b[start + 2];
                return 3 + byteCount + 2;              // 总长 = 3 + 字节数 + 2
            }
            return -1;
        }

        // 步骤2、3：按帧长从缓冲区取出一帧
        // 切出来先校验CRC，通过才取走；失败说明错位，滑动1字节重新对齐。
        static List<byte[]> DrainInLock()
        {
            if (!Monitor.IsEntered(gate))
                throw new InvalidOperationException("DrainInLock 必须在 lock(gate) 内调用");
            var frames = new List<byte[]>();
            while (buf.Count >= 3)
            {
                int len = FrameLength(buf, 0);
                if (len < 0) break;
                if (buf.Count < len) break;

                byte[] frame = new byte[len];
                buf.CopyTo(0, frame, 0, len);

                if (CrcOk(frame))
                {
                    buf.RemoveRange(0, len);
                    frames.Add(frame);
                }
                else
                {
                    Console.WriteLine("[对齐] CRC失败，滑动1字节重新对齐");
                    buf.RemoveRange(0, 1);
                }
            }
            return frames;
        }

        static void Drain()
        {
            List<byte[]> frames;
            lock (gate) { frames = DrainInLock(); }
            foreach (var f in frames) Handle(f);
        }

        // 校验 CRC + 动态解析一整帧
        static void Handle(byte[] frame)
        {
            if (Verbose) Console.WriteLine($"[收到帧] {Hex(frame)}");

            if (frame.Length < 4)
            {
                Console.WriteLine("[错误] 帧太短，丢弃");
                return;
            }

            ushort calc = Crc16(frame, frame.Length - 2);
            ushort recv = (ushort)(frame[frame.Length - 2] | (frame[frame.Length - 1] << 8));
            if (calc != recv)
            {
                Console.WriteLine($"[CRC错误] 算出 0x{calc:X4}，收到 0x{recv:X4}，丢弃");
                return;
            }

            byte func = frame[1];

            // ★ 多从站校验：应答帧的第1字节是"应答者地址"。
            //   写线圈(0x05/0x85)用 activeWrite.Slave 校验，
            //   0x02 验证读(0x02/0x82 且 activeVerify!=null)用 activeVerify.Slave 校验，
            //   事件触发读(0x03/0x04 且 activeRead!=null)用 activeRead.Slave 校验，
            //   周期轮询读用 curSlave 校验。
            //   只有"当前正在等的从站"的应答才算数；别的从站的迟到应答直接忽略，
            //   否则会错配（比如轮到从站2时，从站1的迟到应答被误当成从站2的）。
            byte expect;
            if (func == 0x05 || func == 0x85)
                expect = activeWrite?.Slave ?? 0;
            else if (func == 0x02 || func == 0x82)
                expect = activeVerify?.Slave ?? 0;
            else if (activeRead != null)
                expect = activeRead.Slave;
            else
                expect = curSlave;
            if (frame[0] != expect)
            {
                Console.WriteLine($"[忽略] 从站 {frame[0]:X2} 的迟到应答（当前在等从站 {expect:X2}）");
                return;   // 不Set，发送线程继续等/超时
            }

            // 异常响应（功能码最高位置1，原始功能码 = func & 0x7F）
            if ((func & 0x80) != 0)
            {
                byte orig = (byte)(func & 0x7F);
                Console.WriteLine($"[异常响应·从站{frame[0]:X2}] 功能码 0x{func:X2}（原 0x{orig:X2}），异常码 0x{frame[2]:X2}");
                if (orig == 0x05)
                {
                    // ★ 写异常：通知当前活跃写请求（别死等），AckOk=false 触发重试
                    var aw = activeWrite;
                    if (aw != null) { aw.AckOk = false; aw.Ack.Set(); }
                }
                else if (orig == 0x02)
                {
                    // ★ 0x02 验证读异常：通知当前活跃验证请求，AckOk=false 触发重试
                    var av = activeVerify;
                    if (av != null) { av.AckOk = false; av.Ack.Set(); }
                }
                else if (orig == 0x03 || orig == 0x04)
                {
                    // ★ 读异常：通知当前活跃读请求（别死等），AckOk=false 触发重试
                    var ar = activeRead;
                    if (ar != null) { ar.AckOk = false; ar.Ack.Set(); }
                    else if (pollActive) pollAck.Set();       // 周期轮询读异常
                    // else：无活跃请求（已超时），丢弃，不 Set 任何信号
                }
                else
                {
                    if (pollActive) pollAck.Set();
                    // else：无活跃请求（已超时），丢弃，不 Set 任何信号
                }
                return;
            }

            // ★ 0x05 写单个线圈应答：8字节回显，CRC已在前面校验过。
            //   比对回显帧与当前活跃写请求的发送帧，过滤旧请求的迟到应答（整帧比对比只比对从站号更严格）。
            if (func == 0x05)
            {
                var aw = activeWrite;
                var cur = aw?.Frame;
                if (aw == null || cur == null || frame.Length != cur.Length)
                {
                    if (Verbose) Console.WriteLine($"[忽略·0x05] 没有活跃写请求或帧长度不匹配，丢弃");
                    return;
                }
                for (int i = 0; i < frame.Length; i++)
                {
                    if (frame[i] != cur[i])
                    {
                        if (Verbose) Console.WriteLine($"[忽略·0x05] 回显帧与当前请求不一致（旧应答），丢弃");
                        return;
                    }
                }
                ushort addr = (ushort)((frame[2] << 8) | frame[3]);
                bool on = (frame[4] == 0xFF && frame[5] == 0x00);
                Console.WriteLine($"[写线圈成功·从站{frame[0]:X2}] 线圈{addr} = {(on ? "ON(开到位)" : "OFF(关到位)")}");
                aw.AckOk = true;
                aw.Ack.Set();
                return;
            }

            // ★★★ 0x02 读离散输入应答：强闭环验证——确认物理设备真的到位了。
            //   应答格式：从站 + 0x02 + 字节数 + 数据字节(位打包，LSB在前) + CRC
            //   只读 1 个离散输入，数据 1 字节，bit0 就是状态。
            if (func == 0x02)
            {
                var av = activeVerify;
                if (av == null)
                {
                    if (Verbose) Console.WriteLine($"[忽略·0x02] 没有活跃验证请求，丢弃");
                    return;
                }
                int byteCount = frame[2];
                if (byteCount < 1)
                {
                    Console.WriteLine($"[验证错误·从站{frame[0]:X2}] 0x02 应答字节数为 0，丢弃");
                    av.AckOk = false;
                    av.Ack.Set();
                    return;
                }
                // 位打包：第1字节的 bit0 是第1个离散输入的值
                av.ActualOn = (frame[3] & 0x01) != 0;
                av.AckOk = true;
                av.Ack.Set();
                return;
            }

            // 03/04 读寄存器应答：按"字节数"字段动态解析 N 个寄存器
            if (func == 0x03 || func == 0x04)
            {
                int byteCount = frame[2];
                int n = byteCount / 2;
                if (Verbose)
                {
                    Console.Write($"[解析·从站{frame[0]:X2}] 共{n}个寄存器：");
                    for (int i = 0; i < n; i++)
                    {
                        ushort v = (ushort)((frame[3 + i * 2] << 8) | frame[4 + i * 2]);
                        Console.Write($"[{i}]={v} ");
                    }
                    Console.WriteLine();
                }

                // ★ 事件触发读（开到位后的设备数据采集）：按 activeRead 的 Tag/Scale/Unit 输出工程量
                var ar = activeRead;
                if (ar != null)
                {
                    ar.Values = new ushort[n];
                    for (int i = 0; i < n; i++)
                        ar.Values[i] = (ushort)((frame[3 + i * 2] << 8) | frame[4 + i * 2]);

                    // 输出每个寄存器的工程量值
                    for (int i = 0; i < n; i++)
                    {
                        double eng = ar.Values[i] * ar.Scale;
                        Console.WriteLine($"[采集结果·从站{frame[0]:X2}] {ar.Tag} = {eng} {ar.Unit}（原始值={ar.Values[i]}）");
                    }
                    ar.AckOk = true;
                    ar.Ack.Set();
                    return;
                }

                // 周期轮询读：按原逻辑（温湿度）解析
                if (n >= 2)
                {
                    ushort t = (ushort)((frame[3] << 8) | frame[4]);
                    ushort h = (ushort)((frame[5] << 8) | frame[6]);
                    if (Verbose) Console.WriteLine($"[温湿度·从站{frame[0]:X2}] 温度 {t / 10.0} ℃，湿度 {h / 10.0} %RH");
                }
                // ★★★ 用独立信号 pollAck + 活跃标记 pollActive，替代全局 frameReceived.Set()。
                //   只有 pollActive==true（有活跃的周期轮询请求）才 Set pollAck。
                //   超时后 pollActive=false，迟到应答到达时 pollActive 已 false → 不 Set pollAck。
                //   ★ bool 不存在溢出问题（只有 true/false 两个值），比 int 序号更健壮。
                //   ★ 残留窗口分析：
                //     时序1：Handle 读 curSlave(旧值1) → SendLoop 超时置 pollActive=false →
                //           Handle 校验通过 → Handle 检查 pollActive(false) → 不 Set → 安全。
                //     时序2：Handle 读 curSlave(旧值1) → Handle 校验通过 → Handle 检查 pollActive(true) →
                //           Set pollAck → SendLoop 超时置 pollActive=false → SendLoop pollAck.Reset() → 灯灭 → 安全。
                if (pollActive)
                    pollAck.Set();
                // else：无活跃请求（已超时），丢弃，不 Set 任何信号
            }
            else
            {
                Console.WriteLine("[提示] 非预期的帧");
            }
        }

        // 校验一帧的CRC是否正确（最后2字节是CRC，低字节先）
        static bool CrcOk(byte[] frame)
        {
            ushort calc = Crc16(frame, frame.Length - 2);
            ushort recv = (ushort)(frame[frame.Length - 2] | (frame[frame.Length - 1] << 8));
            return calc == recv;
        }

        // CRC16 计算（对前 len 字节），初值FFFF，多项式0xA001
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

        // 给 data 追加CRC，返回完整帧（低字节先）
        static byte[] Crc(byte[] data)
        {
            ushort crc = Crc16(data, data.Length);
            byte[] r = new byte[data.Length + 2];
            Array.Copy(data, r, data.Length);
            r[data.Length] = (byte)(crc & 0xFF);
            r[data.Length + 1] = (byte)(crc >> 8);
            return r;
        }

        static string Hex(byte[] b) => BitConverter.ToString(b);

        // ★ 写单个线圈（功能码0x05）：on=true 开到位(0xFF00)，on=false 关到位(0x0000)
        //   ★ 多按钮支持：本方法只构造请求投递到 writeQueue，然后阻塞等自己的 Completed 信号。
        //     真正的发送/等回显/重试由 WriteLoop 串行执行，保证 RS485 总线一次一帧。
        //     多个按钮/命令并发调用本方法，各自等各自的 Completed，互不干扰。
        static bool WriteSingleCoil(byte slave, ushort addr, bool on)
        {
            byte[] req = { slave, 0x05, (byte)(addr >> 8), (byte)(addr & 0xFF), (byte)(on ? 0xFF : 0x00), 0x00 };
            byte[] frame = Crc(req);

            var wr = new WriteRequest
            {
                Slave = slave,
                Addr = addr,
                On = on,
                Frame = frame,
            };
            writeQueue.Enqueue(wr);

            // ★ 调用方阻塞等最终结果（参数模式/单按钮场景需要同步拿结果）。
            //   等待上限 = WriteRetry * (发送 + 超时 + 间隔)，留足重试时间。
            int waitMs = WriteRetry * (TimeoutMs + IntervalMs + 50) + 500;
            if (!wr.Completed.Wait(waitMs))
            {
                Console.WriteLine($"[写失败·从站{slave:X2}] 排队执行超时（{waitMs}ms），可能写队列拥堵");
                return false;
            }
            return wr.Result;
        }

        // ★ 写线程：串行消费 writeQueue，一次只处理一个请求，保证 RS485 半双工总线一次一帧。
        //   多个按钮并发投递的请求在这里排队执行；每个请求独立等自己的回显，不会错配。
        //   ★ 用 lock(busLock) 阻塞等总线锁：读事务执行时写等待，保证写命令一定能执行。
        static void WriteLoop()
        {
            while (running)
            {
                if (!writeQueue.TryDequeue(out var wr))
                {
                    Thread.Sleep(5);      // 队列空，短睡避免空转
                    continue;
                }

                lock (busLock)            // ★ 写事务独占总线（含等回显），读事务 TryEnter 失败跳过
                {
                    wr.Result = ExecuteWrite(wr);
                }
                wr.Completed.Set();       // 通知调用方（WriteSingleCoil）
            }
        }

        // ★ 实际执行一次写请求（含重试）。由 WriteLoop 串行调用，同一时刻只有一个活跃请求。
        //   ★ finally 清 activeWrite：防止写完成后迟到的回显拿旧 wr 比对（虽然整帧比对会拒绝，但清掉更干净）。
        //   ★ 写成功后查 ReadProfiles，若该线圈有配置，投递读任务到 readQueue（异步采集设备应答数据）。
        static bool ExecuteWrite(WriteRequest wr)
        {
            byte slave = wr.Slave;
            ushort addr = wr.Addr;
            bool on = wr.On;
            byte[] frame = wr.Frame;

            try
            {
                for (int attempt = 1; attempt <= WriteRetry; attempt++)
                {
                    // ★ 每次发前清干净接收状态，防止上一轮的迟到应答/残帧干扰本轮判定
                    //   锁顺序 io→gate（与 OnData 一致），不会死锁。
                    ClearRxState();
                    wr.Ack.Reset();

                    // ★ 设为活跃请求：Handle 收到 0x05 回显时按此分发
                    activeWrite = wr;

                    // ★ 发送也包进重试内：写超时/串口异常属于临时性故障，应重发而非直接放弃
                    bool sendFailed = false;
                    try
                    {
                        if (!SendFrame(frame)) return false;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[写异常·从站{slave:X2}] 第{attempt}次 发送失败：{ex.Message}，继续重试");
                        sendFailed = true;
                    }

                    if (!sendFailed)
                    {
                        string tag = attempt == 1 ? "写入" : $"重试{attempt}";
                        Console.WriteLine($"[{tag}→从站{slave:X2}] 线圈{addr} = {(on ? "ON(开到位)" : "OFF(关到位)")} {Hex(frame)}");

                        if (wr.Ack.WaitOne(TimeoutMs))
                        {
                            if (wr.AckOk)
                            {
                                // ★★★ 强闭环验证：0x05 回显只表示"从站收到命令"，
                                //   不表示"设备真的启动了"。读 0x02 离散输入验证物理状态。
                                //   验证通过才算"真正开到位"，验证失败则重试写命令。
                                if (on && !VerifyPhysicalState(slave, addr))
                                {
                                    Console.WriteLine($"[验证失败·从站{slave:X2}] 线圈{addr} 写成功但物理状态未到位，继续重试");
                                    continue;   // 物理状态未到位，重试写命令
                                }
                                // ★ 验证通过（或关到位无需验证），投递设备数据采集读任务
                                if (on) EnqueueReadTasks(slave, addr);
                                return true;          // 真正到位确认
                            }
                            Console.WriteLine($"[写异常·从站{slave:X2}] 从站返回异常响应，继续重试");
                        }
                        else
                        {
                            Console.WriteLine($"[写超时·从站{slave:X2}] 第{attempt}次 {TimeoutMs}ms 没回显，继续重试");
                        }
                    }
                    Thread.Sleep(IntervalMs);              // 重试间隔，避免连续轰炸总线
                }
                Console.WriteLine($"[写失败·从站{slave:X2}] 重试{WriteRetry}次仍未成功");
                return false;
            }
            finally
            {
                activeWrite = null;        // ★ 写完清活跃引用，防止迟到回显拿旧 wr 比对
                activeVerify = null;       // ★ 清验证引用，防止迟到 0x02 应答拿旧 vr 比对
            }
        }

        // ★★★ 强闭环验证：写线圈成功后，读 0x02 离散输入验证物理设备真的到位了。
        //   在 ExecuteWrite 内同步执行（已在 busLock 内），验证通过返回 true，失败返回 false。
        //   ★ 没有配置验证表的线圈直接返回 true（向后兼容，不强制所有线圈都验证）。
        //   ★ 验证含重试：设备物理响应可能延迟，VerifyDelayMs 后读，读失败重试 WriteRetry 次。
        static bool VerifyPhysicalState(byte slave, ushort coilAddr)
        {
            // 没有配置验证的线圈，直接返回 true（向后兼容）
            if (!VerifyProfiles.TryGetValue((slave, coilAddr), out var vp))
                return true;

            // 延迟等设备物理响应（如电机启动需要时间）
            if (vp.VerifyDelayMs > 0)
                Thread.Sleep(vp.VerifyDelayMs);

            // 构造 0x02 读离散输入请求：从站 + 0x02 + 起始地址(2字节) + 数量1(2字节) + CRC
            byte[] req = { slave, 0x02, (byte)(vp.VerifyAddr >> 8), (byte)(vp.VerifyAddr & 0xFF), 0x00, 0x01 };
            byte[] frame = Crc(req);

            for (int attempt = 1; attempt <= WriteRetry; attempt++)
            {
                ClearRxState();
                var vr = new VerifyRequest
                {
                    Slave = slave,
                    InputAddr = vp.VerifyAddr,
                    ExpectOn = vp.VerifyOnValue,
                    Frame = frame,
                };
                activeVerify = vr;
                vr.Ack.Reset();

                bool sendFailed = false;
                try
                {
                    if (!SendFrame(frame)) return false;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[验证发送异常·从站{slave:X2}] 第{attempt}次 {ex.Message}，继续重试");
                    sendFailed = true;
                }

                if (!sendFailed)
                {
                    if (Verbose) Console.WriteLine($"[验证→从站{slave:X2}] 读离散输入{vp.VerifyAddr} 第{attempt}次");

                    if (vr.Ack.WaitOne(TimeoutMs))
                    {
                        if (vr.AckOk)
                        {
                            bool matched = (vr.ActualOn == vp.VerifyOnValue);
                            Console.WriteLine($"[验证结果·从站{slave:X2}] 离散输入{vp.VerifyAddr} = {(vr.ActualOn ? "ON" : "OFF")}，期望{(vp.VerifyOnValue ? "ON" : "OFF")}，{(matched ? "✓到位确认" : "✗未到位")}");
                            return matched;
                        }
                        Console.WriteLine($"[验证异常·从站{slave:X2}] 从站返回异常响应，继续重试");
                    }
                    else
                    {
                        Console.WriteLine($"[验证超时·从站{slave:X2}] 第{attempt}次 {TimeoutMs}ms 没应答，继续重试");
                    }
                }
                Thread.Sleep(IntervalMs);
            }
            Console.WriteLine($"[验证失败·从站{slave:X2}] 重试{WriteRetry}次仍未确认物理到位");
            return false;
        }

        // ★ 开到位成功后，按 ReadProfiles 配置投递设备数据采集读任务到 readQueue。
        //   读任务带 DelayMs（等设备数据就绪，如电机转速上升），到点后由 SendLoop 执行。
        //   异步投递不阻塞写线程，读任务在 SendLoop 排队执行。
        static void EnqueueReadTasks(byte slave, ushort coilAddr)
        {
            if (!ReadProfiles.TryGetValue((slave, coilAddr), out var profiles)) return;
            foreach (var p in profiles)
            {
                // 构造 0x03 读保持寄存器请求帧
                byte[] req = { slave, 0x03, (byte)(p.StartAddr >> 8), (byte)(p.StartAddr & 0xFF),
                               (byte)(p.RegCount >> 8), (byte)(p.RegCount & 0xFF) };
                byte[] frame = Crc(req);
                var rr = new ReadRequest
                {
                    Slave = slave,
                    StartAddr = p.StartAddr,
                    RegCount = p.RegCount,
                    Tag = p.Tag,
                    Scale = p.Scale,
                    Unit = p.Unit,
                    Frame = frame,
                };
                // ★ 延迟投递：开到位后等设备数据就绪再读。
                //   用 ThreadPool 异步等 DelayMs 后入队，不阻塞当前写线程。
                int delay = p.DelayMs;
                if (delay <= 0)
                {
                    readQueue.Enqueue(rr);
                    Console.WriteLine($"[采集任务] 从站{slave:X2} {p.Tag}@0x{p.StartAddr:X4} 已投递");
                }
                else
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        Thread.Sleep(delay);
                        readQueue.Enqueue(rr);
                        Console.WriteLine($"[采集任务] 从站{slave:X2} {p.Tag}@0x{p.StartAddr:X4} 延迟{delay}ms后已投递");
                    });
                }
            }
        }

        // ★ 执行一次读请求（发 0x03 + 等应答 + 重试）。由 SendLoop 在 busLock 内调用。
        //   与 ExecuteWrite 同构：清状态 → 设活跃 → 发 → 等应答 → 成功返回。
        static bool ExecuteRead(ReadRequest rr)
        {
            byte slave = rr.Slave;
            byte[] frame = rr.Frame;

            try
            {
                for (int attempt = 1; attempt <= WriteRetry; attempt++)
                {
                    ClearRxState();
                    rr.Ack.Reset();

                    activeRead = rr;
                    curSlave = slave;   // ★ 告诉接收线程：现在在等这个从站

                    bool sendFailed = false;
                    try
                    {
                        if (!SendFrame(frame)) return false;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[读异常·从站{slave:X2}] 第{attempt}次 发送失败：{ex.Message}，继续重试");
                        sendFailed = true;
                    }

                    if (!sendFailed)
                    {
                        if (Verbose) Console.WriteLine($"[采集→从站{slave:X2}] {rr.Tag}@0x{rr.StartAddr:X4} {Hex(frame)}");

                        if (rr.Ack.WaitOne(TimeoutMs))
                        {
                            if (rr.AckOk) return true;
                            Console.WriteLine($"[读异常·从站{slave:X2}] 从站返回异常响应，继续重试");
                        }
                        else
                        {
                            Console.WriteLine($"[读超时·从站{slave:X2}] 第{attempt}次 {TimeoutMs}ms 没应答，继续重试");
                            // ★ 同 SendLoop 超时修复：置 curSlave 无效，防止迟到应答误 Set pollAck
                            curSlave = 0xFF;
                        }
                    }
                    Thread.Sleep(IntervalMs);
                }
                Console.WriteLine($"[读失败·从站{slave:X2}] {rr.Tag} 重试{WriteRetry}次仍未成功");
                return false;
            }
            finally
            {
                activeRead = null;
            }
        }

        // 开到位：写线圈 ON
        static bool SwitchOn(byte slave, ushort addr) => WriteSingleCoil(slave, addr, true);

        // 关到位：写线圈 OFF
        static bool SwitchOff(byte slave, ushort addr) => WriteSingleCoil(slave, addr, false);

        // ★ 命令线程：解析控制台输入，执行开关的开到位/关到位
        //   ★ 多按钮同时操作支持：
        //     - 每条命令异步提交到 writeQueue，立即返回"已提交"，用户可连续输入下一条。
        //     - 多条命令在写线程排队串行执行（RS485 半双工要求），各自结果异步打印。
        //     - 一行可用 ; 分隔多条命令，模拟"同时按多个按钮"。
        //   用法：  on 1 0           → 对从站01、线圈0 开到位
        //           off 1 0          → 对从站01、线圈0 关到位
        //           on 1 0; off 2 0  → 同时提交两个写，排队执行
        //           sync on 1 0      → 同步模式，等结果再返回（默认异步）
        //           quit             → 退出
        static void CmdLoop()
        {
            Console.WriteLine("[命令] 可输入：on <从站> <线圈> / off <从站> <线圈> / 多条用 ; 分隔 / quit");
            Console.WriteLine("[命令] 默认异步提交（立即可输下一条）；前缀 sync 同步等结果");
            while (running)
            {
                string line;
                try { line = Console.ReadLine(); }
                catch (Exception ex) { Console.WriteLine($"[命令] 读取输入异常：{ex.Message}"); break; }
                if (line == null) break;

                // ★ 一行可含多条命令，用 ; 分隔，模拟多个按钮同时按下
                string[] cmds = line.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var one in cmds)
                {
                    ParseAndSubmit(one.Trim());
                }
            }
        }

        // ★ 解析单条命令并提交（异步或同步）
        static void ParseAndSubmit(string line)
        {
            string[] p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) return;
            string cmd = p[0].ToLowerInvariant();

            // 同步模式前缀：sync on 1 0
            bool sync = false;
            if (cmd == "sync")
            {
                sync = true;
                if (p.Length < 2) { Console.WriteLine("[命令] sync 后需跟 on/off"); return; }
                cmd = p[1].ToLowerInvariant();
                // 把 p 往前挪一位
                var tmp = new string[p.Length - 1];
                Array.Copy(p, 1, tmp, 0, tmp.Length);
                p = tmp;
            }

            if (cmd == "quit") { running = false; return; }
            if (p.Length < 3 || !byte.TryParse(p[1], out byte slave) || !ushort.TryParse(p[2], out ushort addr))
            {
                Console.WriteLine("[命令] 格式不对，示例：on 1 0 或 on 1 0; off 2 1");
                return;
            }
            bool on;
            if (cmd == "on") on = true;
            else if (cmd == "off") on = false;
            else { Console.WriteLine($"[命令] 未知指令 {cmd}，只支持 on/off"); return; }

            if (sync)
            {
                // 同步：阻塞等结果
                bool ok;
                try { ok = WriteSingleCoil(slave, addr, on); }
                catch (Exception ex) { Console.WriteLine($"[命令] 执行异常：{ex.Message}"); return; }
                Console.WriteLine(ok ? $"[命令] 从站{slave:X2} 线圈{addr} {(on ? "ON" : "OFF")} 执行成功"
                                     : $"[命令] 从站{slave:X2} 线圈{addr} {(on ? "ON" : "OFF")} 执行失败");
            }
            else
            {
                // ★ 异步：投递到队列，立即返回；结果在后台线程打印
                byte[] req = { slave, 0x05, (byte)(addr >> 8), (byte)(addr & 0xFF), (byte)(on ? 0xFF : 0x00), 0x00 };
                byte[] frame = Crc(req);
                var wr = new WriteRequest { Slave = slave, Addr = addr, On = on, Frame = frame };
               var xx2= wr.GetHashCode();
                writeQueue.Enqueue(wr);
                Console.WriteLine($"[已提交] 从站{slave:X2} 线圈{addr} = {(on ? "ON(开到位)" : "OFF(关到位)")}，排队中...");

                // 后台线程等结果并打印
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    int waitMs = WriteRetry * (TimeoutMs + IntervalMs + 50) + 500;
                    var xx = wr.GetHashCode();
                    if (!wr.Completed.Wait(waitMs))
                    {
                        Console.WriteLine($"[结果] 从站{slave:X2} 线圈{addr} {(on ? "ON" : "OFF")} 排队超时");
                        return;
                    }
                    Console.WriteLine(wr.Result
                        ? $"[结果] 从站{slave:X2} 线圈{addr} {(on ? "ON" : "OFF")} 执行成功 ✓"
                        : $"[结果] 从站{slave:X2} 线圈{addr} {(on ? "ON" : "OFF")} 执行失败 ✗");
                });
            }
        }
    }
}
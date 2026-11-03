//using System;
//using System.Collections.Generic;
//using System.Net.Sockets;
//using System.Threading;

//namespace chenbo
//{
//    class Program
//    {
//        static readonly List<byte> buf = new List<byte>();
//        static readonly object gate = new object();

//        static TcpClient client;
//        static NetworkStream stream;
//        static volatile bool running = true;

//        static readonly AutoResetEvent frameReceived = new AutoResetEvent(false);

//        static readonly byte[] Slaves = { 0x01, 0x02 };

//        static volatile byte curSlave;
//        static ushort curTid;
//        static ushort nextTid;   // 事务标识自增，到65535回绕到0（默认unchecked回绕不抛异常）

//        const string Ip = "127.0.0.1";
//        const int Port = 502;
//        const int RegCount = 2;
//        const int TimeoutMs = 1000;
//        const int IntervalMs = 100;

//        static void Main(string[] args)
//        {
//            client = new TcpClient();
//            try { client.Connect(Ip, Port); }
//            catch (Exception ex)
//            {
//                Console.WriteLine($"连接失败 {Ip}:{Port} - {ex.Message}");
//                return;
//            }
//            stream = client.GetStream();
//            Console.WriteLine($"已连接 {Ip}:{Port}，开始轮询从站 {string.Join(",", Array.ConvertAll(Slaves, b => $"0x{b:X2}"))}...");

//            Console.CancelKeyPress += (s, e) => { e.Cancel = true; running = false; };

//            Thread tx = new Thread(SendLoop) { IsBackground = true };
//            tx.Start();

//            Thread rx = new Thread(ReceiveLoop) { IsBackground = true };
//            rx.Start();

//            while (running) Thread.Sleep(200);

//            try { stream?.Close(); } catch { }
//            try { client?.Close(); } catch { }
//            Console.WriteLine("已退出。");
//        }

//        static void SendLoop()
//        {
//            int idx = 0;
//            while (running)
//            {
//                byte id = Slaves[idx];
//                ushort tid = ++nextTid;
//                curSlave = id;
//                curTid = tid;

//                byte[] req = BuildRequest(tid, id, 0x03, 0x0000, RegCount);
//                try { stream.Write(req, 0, req.Length); }
//                catch { Console.WriteLine("[发送失败] 连接断开"); running = false; break; }
//                Console.WriteLine($"[发送→从站{id:X2} TID={tid}] {Hex(req)}");

//                if (!frameReceived.WaitOne(TimeoutMs))
//                {
//                    Console.WriteLine($"[超时] 从站{id:X2} {TimeoutMs}ms没应答，清空接收状态后轮下一个");
//                    lock (gate) { buf.Clear(); }
//                    frameReceived.Reset();
//                }

//                idx = (idx + 1) % Slaves.Length;
//                Thread.Sleep(IntervalMs);
//            }
//        }

//        static void ReceiveLoop()
//        {
//            byte[] tmp = new byte[1024];
//            while (running)
//            {
//                int n;
//                try { n = stream.Read(tmp, 0, tmp.Length); }
//                catch { break; }
//                if (n == 0) break;

//                byte[] seg = new byte[n];
//                Array.Copy(tmp, seg, n);
//                lock (gate) { buf.AddRange(seg); }
//                Drain();
//            }
//            Console.WriteLine("[连接断开] 接收线程退出");
//            running = false;
//        }

//        static void Drain()
//        {
//            var frames = new List<byte[]>();
//            lock (gate)
//            {
//                while (buf.Count >= 6)
//                {
//                    int length = (buf[4] << 8) | buf[5];
//                    int total = 6 + length;
//                    if (buf.Count < total) break;

//                    byte[] frame = new byte[total];
//                    buf.CopyTo(0, frame, 0, total);
//                    buf.RemoveRange(0, total);
//                    frames.Add(frame);
//                }
//            }
//            foreach (var f in frames) Handle(f);
//        }

//        static void Handle(byte[] frame)
//        {
//            Console.WriteLine($"[收到帧] {Hex(frame)}");

//            if (frame.Length < 8)
//            {
//                Console.WriteLine("[错误] 帧太短，丢弃");
//                return;
//            }

//            ushort tid = (ushort)((frame[0] << 8) | frame[1]);
//            ushort proto = (ushort)((frame[2] << 8) | frame[3]);
//            int length = (frame[4] << 8) | frame[5];
//            byte unitId = frame[6];
//            byte func = frame[7];

//            if (proto != 0)
//            {
//                Console.WriteLine($"[错误] 协议ID={proto}非Modbus，丢弃");
//                return;
//            }

//            if (tid != curTid)
//            {
//                Console.WriteLine($"[忽略] TID={tid}不匹配(当前{curTid})，迟到/错配响应");
//                return;
//            }

//            if (unitId != curSlave)
//            {
//                Console.WriteLine($"[忽略] 从站{unitId:X2}迟到应答(当前{curSlave:X2})");
//                return;
//            }

//            if ((func & 0x80) != 0)
//            {
//                Console.WriteLine($"[异常响应·从站{unitId:X2}] 功能码0x{func:X2}，异常码0x{frame[8]:X2}");
//                frameReceived.Set();
//                return;
//            }

//            if (func == 0x03 || func == 0x04)
//            {
//                int byteCount = frame[8];
//                int n = byteCount / 2;
//                Console.Write($"[解析·从站{unitId:X2}] 共{n}个寄存器：");
//                for (int i = 0; i < n; i++)
//                {
//                    ushort v = (ushort)((frame[9 + i * 2] << 8) | frame[10 + i * 2]);
//                    Console.Write($"[{i}]={v} ");
//                }
//                Console.WriteLine();

//                if (n >= 2)
//                {
//                    ushort t = (ushort)((frame[9] << 8) | frame[10]);
//                    ushort h = (ushort)((frame[11] << 8) | frame[12]);
//                    Console.WriteLine($"[温湿度·从站{unitId:X2}] 温度{t / 10.0}℃，湿度{h / 10.0}%RH");
//                }
//                frameReceived.Set();
//            }
//            else
//            {
//                Console.WriteLine($"[提示] 非预期功能码 0x{func:X2}");
//            }
//        }

//        static byte[] BuildRequest(ushort tid, byte unitId, byte func, ushort start, ushort count)
//        {
//            byte[] req = new byte[12];
//            req[0] = (byte)(tid >> 8);
//            req[1] = (byte)(tid & 0xFF);
//            req[2] = 0; req[3] = 0;
//            req[4] = 0; req[5] = 6;
//            req[6] = unitId;
//            req[7] = func;
//            req[8] = (byte)(start >> 8);
//            req[9] = (byte)(start & 0xFF);
//            req[10] = (byte)(count >> 8);
//            req[11] = (byte)(count & 0xFF);
//            return req;
//        }

//        static string Hex(byte[] b) => BitConverter.ToString(b);
//    }
//}

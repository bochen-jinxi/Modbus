using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;

namespace SlaveSim
{
    class Program
    {
        static readonly List<byte> buf = new List<byte>();
        static readonly object gate = new object();
        static SerialPort sp;
        static volatile bool running = true;

        static void Main(string[] args)
        {
            string port = args.Length > 0 ? args[0] : "COM3";
            int baud = args.Length > 1 ? int.Parse(args[1]) : 9600;
            byte slave = args.Length > 2 ? byte.Parse(args[2]) : (byte)1;

            sp = new SerialPort(port, baud, Parity.None, 8, StopBits.One);
            sp.WriteTimeout = 1000;
            sp.DataReceived += OnData;
            sp.Open();
            sp.DiscardInBuffer();
            Console.WriteLine($"[从站模拟器] {port} {baud}bps 从站地址={slave}，等待请求...");
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; running = false; };
            while (running) Thread.Sleep(200);
            if (sp.IsOpen) sp.Close();
            Console.WriteLine("[从站模拟器] 已退出。");
        }

        static void OnData(object sender, SerialDataReceivedEventArgs e)
        {
            int n = sp.BytesToRead;
            if (n == 0) return;
            byte[] tmp = new byte[n];
            sp.Read(tmp, 0, n);
            lock (gate) { buf.AddRange(tmp); }
            Thread.Sleep(5);
            List<byte[]> frames;
            lock (gate) { frames = DrainInLock(); }
            foreach (var f in frames) Handle(f);
        }

        static List<byte[]> DrainInLock()
        {
            var frames = new List<byte[]>();
            while (buf.Count >= 4)
            {
                int len = FrameLen(buf, 0);
                if (len < 0) break;
                if (buf.Count < len) break;
                byte[] f = new byte[len];
                buf.CopyTo(0, f, 0, len);
                buf.RemoveRange(0, len);
                frames.Add(f);
            }
            return frames;
        }

        static int FrameLen(List<byte> b, int s)
        {
            byte func = b[s + 1];
            if ((func & 0x80) != 0) return 5;
            if (func == 0x05 || func == 0x06) return 8;
            if (func == 0x03 || func == 0x04) return 8;
            if (func == 0x0F) return 9 + (b[s + 6] | (b[s + 7] << 8)) / 8 + (((b[s + 6] | (b[s + 7] << 8)) % 8) == 0 ? 0 : 1);
            if (func == 0x10) return 9 + b[s + 6];
            return -1;
        }

        static void Handle(byte[] req)
        {
            Console.WriteLine($"[收请求] {BitConverter.ToString(req)}");
            if (!CrcOk(req)) { Console.WriteLine("[CRC错] 丢弃"); return; }
            byte id = req[0];
            byte func = req[1];

            if (func == 0x03 || func == 0x04)
            {
                ushort start = (ushort)((req[2] << 8) | req[3]);
                ushort count = (ushort)((req[4] << 8) | req[5]);
                byte[] resp = new byte[3 + count * 2 + 2];
                resp[0] = id; resp[1] = func; resp[2] = (byte)(count * 2);
                for (int i = 0; i < count; i++)
                {
                    ushort v = (ushort)(start + i == 0 ? 255 : (start + i == 1 ? 605 : 0));
                    resp[3 + i * 2] = (byte)(v >> 8);
                    resp[4 + i * 2] = (byte)(v & 0xFF);
                }
                AddCrc(resp);
                sp.Write(resp, 0, resp.Length);
                Console.WriteLine($"[回应答·0x{func:X2}] {BitConverter.ToString(resp)}");
            }
            else if (func == 0x05 || func == 0x06)
            {
                byte[] resp = new byte[8];
                Array.Copy(req, resp, 6);
                AddCrc(resp);
                sp.Write(resp, 0, resp.Length);
                Console.WriteLine($"[回写回显·0x{func:X2}] {BitConverter.ToString(resp)}");
            }
            else
            {
                byte[] ex = { id, (byte)(func | 0x80), 0x01 };
                AddCrc(ex);
                sp.Write(ex, 0, ex.Length);
                Console.WriteLine($"[异常响应] 不支持的功能码 0x{func:X2}");
            }
        }

        static bool CrcOk(byte[] f)
        {
            ushort calc = Crc16(f, f.Length - 2);
            ushort recv = (ushort)(f[f.Length - 2] | (f[f.Length - 1] << 8));
            return calc == recv;
        }

        static void AddCrc(byte[] f)
        {
            ushort crc = Crc16(f, f.Length - 2);
            f[f.Length - 2] = (byte)(crc & 0xFF);
            f[f.Length - 1] = (byte)(crc >> 8);
        }

        static ushort Crc16(byte[] d, int len)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < len; i++)
            {
                crc ^= d[i];
                for (int j = 0; j < 8; j++)
                    crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
            return crc;
        }
    }
}

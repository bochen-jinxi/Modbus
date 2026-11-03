# -*- coding: utf-8 -*-
# 生成 Modbus RTU 多线程并发与共享内存协调动画：dataflow.mp4 + dataflow.gif
# 画面 = 线程跑道(并发) + 共享内存区(锁/volatile/事件) + 数据点流动
import math
from PIL import Image, ImageDraw, ImageFont
import numpy as np
import imageio.v2 as imageio

W, H, FPS, SEC = 1280, 1100, 15, 24
N = FPS * SEC
FONT = "C:/Windows/Fonts/msyh.ttc"

C_BG    = (15, 20, 32)
C_BOX   = (26, 35, 51)
C_EDGE  = (58, 74, 99)
C_ACT   = (255, 213, 74)
C_TITLE = (232, 238, 247)
C_LBL   = (159, 179, 209)
C_CELL  = (16, 24, 39)
C_CELED = (51, 65, 92)
C_ON    = (46, 134, 255)
C_ONED  = (125, 180, 255)
C_BAR   = (255, 183, 77)
C_LOG   = (143, 211, 160)
C_RED   = (255, 90, 90)
C_WAIT  = (60, 26, 30)
C_GRAY  = (110, 118, 130)

# 线程颜色
C_TX = (124, 252, 0)    # SendLoop 绿
C_RX = (77, 178, 255)   # OnData 蓝
C_FX = (255, 213, 74)   # FrameGapLoop 黄
C_MN = (180, 160, 220)  # Main 紫

f_title = ImageFont.truetype(FONT, 16)
f_lbl   = ImageFont.truetype(FONT, 13)
f_step  = ImageFont.truetype(FONT, 17)
f_log   = ImageFont.truetype(FONT, 14)
f_small = ImageFont.truetype(FONT, 12)
f_sub   = ImageFont.truetype(FONT, 20)

# 运行时刻字幕（电影式解说词，随时间切换）
SUBS = [
    (0.0,  "SendLoop TryEnter(busLock,0) 成功 → 持有总线事务锁，写事务此刻进不来"),
    (0.3,  "拿到 io 锁独占串口，发出 8 字节请求帧 01 03 00 00 00 02 + CRC"),
    (1.4,  "发完就阻塞：SendLoop 在 frameReceived 上等 50ms，跑道变红（仍持 busLock）"),
    (1.6,  "应答字节分 3 批到达，OnData 事件触发，同样拿 io 锁读出来"),
    (3.1,  "每读一批就持 gate 锁追加进 buf，并用 Interlocked 记下最后接收时刻"),
    (4.8,  "字节流停了：FrameGapLoop 发现静默超 3.5 字符(3.65ms)，判定帧结束"),
    (5.0,  "FrameGapLoop 持 gate 锁处理残帧——注意此刻锁是它的颜色(黄)"),
    (5.5,  "迟到字节想进 buf，但 gate 锁被占着，OnData 只能在锁外等待"),
    (6.0,  "锁释放了，OnData 拿到锁，把迟到字节补进 buf"),
    (6.2,  "Drain 按帧长规则切走一整帧：03 应答 = 3 + 字节数 + CRC2 = 9 字节"),
    (7.6,  "Handle 校验 CRC 通过，读 curSlave 比对：是 1 号站的应答，算数"),
    (8.3,  "解析出温度 25.3℃ 湿度 60.5%RH，然后 frameReceived.Set() 亮绿灯"),
    (8.7,  "信号飞回发送线程——AutoResetEvent 被消费后灯自动变回红"),
    (9.0,  "SendLoop 醒来，轮询指针切到 0x02，写 curSlave=02，发下一帧"),
    (10.6, "一轮完成：1 号站问完问 2 号站，如此循环，直到 Ctrl+C"),
]

LANES = [("Main", "Main 线程", C_MN, 70), ("TX", "SendLoop 发送线程", C_TX, 115),
         ("RX", "OnData 接收线程", C_RX, 160), ("FX", "FrameGapLoop 检测线程", C_FX, 205)]
LANE_Y = {k: y for k, _, _, y in LANES}

# 共享变量格: key: (x,y,w,h,标题)
CELLS = {
    "bus":   (60,  250, 360, 55, "busLock  总线事务锁 [TryEnter/lock]"),
    "sp":    (60,  310, 360, 75, "sp 串口        [lock(io)]"),
    "buf":   (460, 310, 360, 75, "buf 接收缓冲   [lock(gate)]"),
    "cur":   (860, 310, 360, 75, "curSlave       [volatile]"),
    "run":   (60,  395, 360, 75, "running        [volatile]"),
    "tick":  (460, 395, 360, 75, "lastDataTicks  [Interlocked]"),
    "evt":   (860, 395, 360, 75, "frameReceived  [AutoResetEvent]"),
}
P_TX = (950, 130); P_RX = (950, 175); P_FX = (950, 220)
P_SP = (240, 355); P_BUF = (640, 355); P_CUR = (1040, 355)
P_TICK = (640, 440); P_EVT = (1040, 440); P_OUT = (640, 545)

STEPS = [
    (0,    "① SendLoop TryEnter(busLock,0) 成功 → 持总线锁 → 持 io 锁发请求帧"),
    (1.5,  "② SendLoop 进入 WaitOne 阻塞(红)；应答字节分 3 批到达，OnData 持 io 锁读取"),
    (3.0,  "③ OnData 持 gate 锁入 buf + Interlocked 写 lastDataTicks；FrameGapLoop 无锁轮询读取"),
    (4.8,  "④ 静默≥3.5字符 → FrameGapLoop 持 gate 判帧尾；迟到字节到来时 OnData 被 gate 锁挡住等待"),
    (6.2,  "⑤ Drain 切走完整帧（03应答=3+N+2=9字节）"),
    (7.6,  "⑥ Handle 读 curSlave(volatile) 比对归属 → CRC OK → 解析温湿度"),
    (8.3,  "⑦ frameReceived.Set() 灯变绿 → 信号飞向 SendLoop"),
    (9.0,  "⑧ SendLoop 被唤醒(AutoReset 灯自动复红) → idx→0x02 发下一帧"),
    (10.6, "—— 一轮完成，循环 ——"),
]

def states(t):
    """返回各线程状态: (mode, 文案)  mode: run/wait/idle"""
    tx = ("wait", "WAIT 等应答(50ms)") if 1.4 <= t < 9.0 else ("run", "")
    if t < 0.4: tx = ("run", "RUN 写curSlave=01")
    elif t < 1.4: tx = ("run", "RUN 持io写串口")
    elif 9.0 <= t < 9.6: tx = ("run", "RUN 唤醒 idx→02")
    elif 9.6 <= t < 10.5: tx = ("run", "RUN 持io写串口")
    elif t >= 10.5: tx = ("wait", "WAIT 等0x02(循环)")

    rx = ("idle", "idle 空闲")
    if 1.6 <= t < 3.05: rx = ("run", "RUN 持io读串口")
    if 3.1 <= t < 4.6: rx = ("run", "RUN 持gate入buf")
    if 5.5 <= t < 6.0: rx = ("wait", "WAIT 等gate锁")
    if 6.0 <= t < 6.3: rx = ("run", "RUN 拿锁入buf")

    fx = ("run", "poll 读lastDataTicks")
    if 4.8 <= t < 6.2: fx = ("run", "RUN 持gate判帧尾")
    if 6.2 <= t < 9.0: fx = ("run", "RUN Drain/Handle")
    mn = ("idle", "Sleep(200) 等Ctrl+C")
    return {"TX": tx, "RX": rx, "FX": fx, "Main": mn}

def io_owner(t):
    if 0.4 <= t < 1.4: return "TX"
    if 1.6 <= t < 2.4 or 2.1 <= t < 2.9 or 2.5 <= t < 3.05: return "RX"
    if 9.6 <= t < 10.5: return "TX"
    return None

def gate_owner(t):
    if 3.1 <= t < 3.8 or 3.5 <= t < 4.2 or 3.9 <= t < 4.6: return "RX"
    if 5.0 <= t < 6.0: return "FX"
    if 6.0 <= t < 6.3: return "RX"
    return None

# busLock 持有者（读流程）：SendLoop TryEnter 成功后持有整轮，直到 Monitor.Exit
# 读流程 t=0~10.5 一轮内 SendLoop 持有 busLock（TryEnter(0) 成功 → finally Exit）
def bus_owner(t):
    if 0.0 <= t < 10.5: return "TX"
    return None

OWNER_C = {"TX": C_TX, "RX": C_RX, "FX": C_FX, "Main": C_MN, "WL": (255, 165, 0), "TX2": C_TX}

def lerp(a, b, t0, t1, t):
    if t < t0 or t > t1: return None
    k = (t - t0) / (t1 - t0)
    return (a[0] + (b[0] - a[0]) * k, a[1] + (b[1] - a[1]) * k)

def dot(d, pos, r, c):
    if pos:
        x, y = pos
        d.ellipse([x - r, y - r, x + r, y + r], fill=c)

def dash(d, a, b, c, w=2, seg=8, gap=6):
    ax, ay = a; bx, by = b
    L = math.hypot(bx - ax, by - ay)
    if L == 0: return
    ux, uy = (bx - ax) / L, (by - ay) / L
    s = 0.0
    while s < L:
        e = min(s + seg, L)
        d.line([ax + ux * s, ay + uy * s, ax + ux * e, ay + uy * e], fill=c, width=w)
        s += seg + gap

def draw_lock(d, x, y, owner):
    """锁图标：owner=None 灰色开锁，否则持有者颜色"""
    c = OWNER_C.get(owner, C_GRAY)
    # 锁梁
    if owner:
        d.arc([x, y, x + 16, y + 16], 180, 360, fill=c, width=3)
    else:
        d.arc([x + 4, y - 2, x + 20, y + 14], 180, 320, fill=c, width=3)
    d.rounded_rectangle([x - 3, y + 8, x + 19, y + 26], 3,
                        fill=c if owner else C_CELL, outline=c, width=2)
    if owner:
        d.ellipse([x + 5, y + 14, x + 11, y + 20], fill=C_BG)

def draw_lanes(d, t, st):
    for key, name, c, y in LANES:
        mode, txt = st[key]
        d.text((40, y + 6), name, font=f_lbl, fill=c)
        bg = C_WAIT if mode == "wait" else C_BOX
        d.rounded_rectangle([200, y, 1090, y + 30], 6, fill=bg, outline=C_EDGE)
        if mode == "run":
            spd = {"FX": 300, "TX": 180, "RX": 220, "Main": 60}[key]
            x = 210 + ((t * spd) % 860)
            d.ellipse([x - 6, y + 9, x + 6, y + 21], fill=c)
        elif mode == "wait":
            d.ellipse([206, y + 9, 218, y + 21], fill=C_RED)
            if int(t * 3) % 2 == 0:
                d.text((230, y + 7), "阻塞中…", font=f_small, fill=C_RED)
        tc = {"run": c, "wait": C_RED, "idle": C_GRAY}[mode]
        d.text((1100, y + 7), txt, font=f_small, fill=tc)

def draw_shared(d, t):
    d.rounded_rectangle([30, 240, 1250, 480], 10, outline=(40, 60, 90), width=2)
    d.text((45, 248), "共享内存 / 临界区", font=f_lbl, fill=C_LBL)
    io_o, gt_o, bus_o = io_owner(t), gate_owner(t), bus_owner(t)
    for key, (x, y, w, h, title) in CELLS.items():
        d.rounded_rectangle([x, y, x + w, y + h], 8, fill=C_BOX, outline=C_EDGE, width=2)
        d.text((x + 12, y + 8), title, font=f_lbl, fill=C_TITLE)
        if key == "bus":
            draw_lock(d, x + w - 40, y + 22, bus_o)
            if bus_o == "TX":
                txt, c = "SendLoop 持有(TryEnter 成功)", C_TX
            elif bus_o == "WL":
                txt, c = "WriteLoop 持有(lock 阻塞等)", C_WL
            else:
                txt, c = "空闲", C_GRAY
            d.text((x + 12, y + 30), txt, font=f_small, fill=c)
        elif key == "sp":
            draw_lock(d, x + w - 40, y + 30, io_o)
            d.text((x + 12, y + 40), "SerialPort COM4", font=f_small, fill=C_LBL)
        elif key == "buf":
            draw_lock(d, x + w - 40, y + 30, gt_o)
            n = 0 if t < 3.1 else 3 if t < 3.5 else 6 if t < 3.9 else 9 if t < 6.0 else 11 if t < 6.3 else 0
            for i in range(8):
                cx = x + 14 + i * 26
                on = i < min(n, 8)
                d.rectangle([cx, y + 38, cx + 20, y + 58],
                            fill=C_ON if on else C_CELL, outline=C_ONED if on else C_CELED)
            if n > 8:
                d.text((x + 230, y + 42), f"{n}B", font=f_small, fill=C_ONED)
            if 5.5 <= t < 6.0 and int(t * 6) % 2 == 0:   # RX 等锁提示
                d.text((x + w - 150, y + 34), "← OnData 等待中", font=f_small, fill=C_RX)
        elif key == "cur":
            v = "0x01"
            if t < 0.05 or t >= 10.0: v = "0x02"
            flash = (0.05 <= t < 0.4) or (9.05 <= t < 10.0)
            cv = C_ACT if flash and int(t * 20) % 2 == 0 else C_TITLE
            d.text((x + 12, y + 38), f"= {v}  (当前等待的从站)", font=f_log, fill=cv)
        elif key == "run":
            d.text((x + 12, y + 38), "= true", font=f_log, fill=C_TITLE)
        elif key == "tick":
            wr = any(abs(t - w0) < 0.15 for w0 in (3.1, 3.5, 3.9, 6.0))
            val = 0 if t < 3.1 else int(t * 10_000_000)
            cv = C_ACT if wr else C_TITLE
            d.text((x + 12, y + 38), f"= {val}  (最后接收时刻)", font=f_log, fill=cv)
        elif key == "evt":
            on = 8.3 <= t < 9.05
            d.ellipse([x + 14, y + 38, x + 34, y + 58],
                      fill=(60, 220, 100) if on else (200, 60, 60))
            d.text((x + 44, y + 40), "已置位(Set)" if on else "未置位", font=f_small,
                   fill=C_LOG if on else C_RED)
            d.text((x + 160, y + 40), "WaitOne 消费后自动复位", font=f_small, fill=C_GRAY)

def draw_links(d, t):
    if 0.05 <= t < 0.4:                       # TX 写 curSlave
        d.line([P_TX[0], P_TX[1], P_CUR[0], P_CUR[1] - 40], fill=C_TX, width=2)
    if 4.8 <= t < 9.0 and int(t * 4) % 2 == 0:  # FX 无锁轮询读 ticks
        dash(d, P_FX, (P_TICK[0], P_TICK[1] - 45), C_FX)
    if 7.8 <= t < 8.6:                        # Handle 读 curSlave
        dash(d, P_FX, (P_CUR[0], P_CUR[1] - 40), C_FX)
    if 8.3 <= t < 8.7:                        # Set
        d.line([P_FX[0], P_FX[1], P_EVT[0], P_EVT[1] - 40], fill=C_FX, width=3)

def draw_dots(d, t):
    dot(d, lerp(P_TX, P_SP, 0.4, 1.4, t), 7, C_TX)         # ① 请求
    for t0, t1 in ((1.6, 2.4), (2.1, 2.9), (2.5, 3.05)):   # ② 应答3批
        dot(d, lerp(P_SP, P_RX, t0, t1, t), 6, C_RX)
    for t0, t1 in ((3.1, 3.8), (3.5, 4.2), (3.9, 4.6)):    # ③ 入buf
        dot(d, lerp(P_RX, P_BUF, t0, t1, t), 6, C_RX)
    dot(d, lerp(P_RX, (760, 355), 5.5, 5.8, t), 6, C_RX)   # ④ 迟到字节被锁挡住
    dot(d, lerp((760, 355), P_BUF, 6.0, 6.3, t), 6, C_RX)  # 拿到锁后进入
    dot(d, lerp(P_FX, P_BUF, 5.0, 5.9, t), 8, C_FX)        # FX 触发
    dot(d, lerp(P_BUF, P_OUT, 6.2, 7.4, t), 8, C_FX)       # ⑤ 整帧切出
    dot(d, lerp(P_EVT, P_TX, 8.7, 9.8, t), 8, (255, 125, 176))  # ⑦ Set信号
    dot(d, lerp(P_TX, P_SP, 9.6, 10.5, t), 7, C_TX)        # ⑧ 下一帧(0x02)

def draw_misc(d, t):
    label = ""
    for ts, s in STEPS:
        if t >= ts: label = s
    d.text((40, 38), label, font=f_step, fill=C_ACT)
    # 静默进度条
    d.text((60, 512), "帧间静默计时(3.5字符=3.65ms)：", font=f_lbl, fill=C_LBL)
    d.rectangle([330, 514, 530, 526], fill=C_CELL, outline=C_CELED)
    if 4.8 <= t < 6.2:
        d.rectangle([330, 514, 330 + (t - 4.8) / 1.4 * 200, 526], fill=C_BAR)
    # 解析输出
    if t >= 7.6:
        d.text((60, 545), "[解析·从站01] 共2个寄存器：[0]=253 [1]=605", font=f_log, fill=C_LOG)
    if t >= 8.3:
        d.text((60, 568), "[温湿度·从站01] 温度 25.3 ℃，湿度 60.5 %RH   CRC OK", font=f_log, fill=C_LOG)
    # BUG 警告（常驻）
    d.text((60, 610), "⚠ 诚实补充·源码两处BUG：189行 lock(gate) 内 Sleep(50000) → 持锁卡死50秒；166行整数除法 → 帧超时永不触发（详见 运行流程分析.txt）",
           font=f_lbl, fill=C_RED)
    # 图例
    ly = 650
    d.text((60, ly), "图例：", font=f_lbl, fill=C_LBL)
    for i, (name, c) in enumerate([("SendLoop", C_TX), ("OnData", C_RX),
                                   ("FrameGapLoop", C_FX), ("Main", C_MN)]):
        x = 130 + i * 150
        d.ellipse([x, ly + 4, x + 12, ly + 16], fill=c)
        d.text((x + 18, ly + 2), name, font=f_small, fill=C_LBL)
    d.text((760, ly), "跑道红底=阻塞等待  锁块颜色=持锁线程  虚线=无锁读(volatile/Interlocked)",
           font=f_small, fill=C_GRAY)
    d.text((760, ly + 22), "io/gate/busLock：同一时刻只允许一个持有者；busLock=总线事务锁(读TryEnter/写lock)",
           font=f_small, fill=C_GRAY)

def draw_subtitle(d, t):
    sub = ""
    for ts, s in SUBS:
        if t >= ts: sub = s
    d.rounded_rectangle([110, 715, 1170, 785], 10, fill=(8, 10, 15), outline=(60, 70, 90), width=2)
    d.text((128, 742), f"t={t:04.1f}s", font=f_lbl, fill=C_ACT)
    w = d.textlength(sub, font=f_sub)
    d.text(((W - w) / 2 + 40, 738), sub, font=f_sub, fill=(245, 245, 245))

def make_frame(t):
    img = Image.new("RGB", (W, H), C_BG)
    d = ImageDraw.Draw(img)
    if t < 12.0:
        d.text((40, 10), "Modbus RTU · 多线程并发与共享内存协调（读流程）", font=f_title, fill=C_TITLE)
        st = states(t)
        draw_lanes(d, t, st)
        draw_shared(d, t)
        draw_links(d, t)
        draw_dots(d, t)
        draw_misc(d, t)
        draw_subtitle(d, t)
    else:
        draw_write_frame(d, t - 12.0)
    return img

# ==================== 写流程（多按钮队列改造） ====================
C_WL  = (255, 165, 0)    # WriteLoop 橙
C_EX  = (255, 110, 110)  # ExecuteWrite 红
C_BTN = (124, 252, 0)    # 按钮/提交方 绿
C_BG2 = (100, 180, 255)  # 后台打印线程 蓝

SUBS_W = [
    (0.0,  "按钮A：new wr1{Completed=信号C1} → writeQueue.Enqueue(wr1) → 立即返回'已提交'"),
    (1.5,  "按钮B：new wr2{Completed=信号C2} → Enqueue → 立即返回（不阻塞，可继续输下一条）"),
    (3.0,  "后台线程A/B 各自 wr.Completed.Wait（闭包捕获自己的 wr，互不干扰）"),
    (4.3,  "WriteLoop lock(busLock) 阻塞等总线锁 → 拿到！SendLoop 此刻 TryEnter 全部失败"),
    (4.5,  "WriteLoop：TryDequeue 取出 wr1 → ExecuteWrite(wr1) → activeWrite=wr1"),
    (5.5,  "ExecuteWrite：lock(io) sp.Write(wr1.Frame) → wr1.Ack.WaitOne 等回显"),
    (6.5,  "SendLoop TryEnter(busLock,0) 失败 → 跳过本轮读 → Sleep(15) → 再试再失败（写优先）"),
    (7.0,  "Handle 收到 0x05 回显 → 比对 activeWrite.Frame 整帧一致 → aw.AckOk=true → aw.Ack.Set()"),
    (8.0,  "ExecuteWrite 被唤醒 → wr1.Result=true → wr1.Completed.Set() → 唤醒后台线程A"),
    (8.3,  "WriteLoop 释放 busLock → SendLoop 下一轮 TryEnter 终于成功，读恢复"),
    (9.5,  "WriteLoop 取 wr2 → lock(busLock) 再次独占 → SendLoop 又被挡开（写优先）"),
    (11.0, "wr2 完成 → wr2.Completed.Set() → 唤醒后台线程B（各等各的，不会错配）"),
    (12.5, "多按钮并发提交、串行执行、独立回传完成；busLock 保证写优先、读让步"),
]

STEPS_W = [
    (0,    "① 按钮A new wr1 → Enqueue → 立即返回"),
    (1.5,  "② 按钮B new wr2 → Enqueue → 立即返回"),
    (3.0,  "③ 后台线程A/B 各自 wr.Completed.Wait"),
    (4.3,  "④ WriteLoop lock(busLock) 独占总线 → SendLoop TryEnter 失败跳过"),
    (5.5,  "⑤ sp.Write(wr1.Frame) → wr1.Ack.WaitOne"),
    (7.0,  "⑥ Handle 比对 activeWrite → aw.Ack.Set()"),
    (8.0,  "⑦ wr1.Completed.Set() → 唤醒后台线程A → 释放 busLock"),
    (8.3,  "⑧ SendLoop TryEnter 成功 → 读恢复一轮"),
    (9.5,  "⑨ WriteLoop 取 wr2 → lock(busLock) 再次独占 → SendLoop 又被挡开"),
    (11.0, "⑩ wr2.Completed.Set() → 唤醒后台线程B"),
    (12.5, "—— 完成 ——"),
]

LANES_W = [("BTN", "按钮A/B CmdLoop", C_BTN, 70), ("WL", "WriteLoop 写线程", C_WL, 115),
           ("EX", "ExecuteWrite", C_EX, 160), ("BG", "后台打印线程", C_BG2, 205),
           ("TX2", "SendLoop 读线程", C_TX, 250)]

CELLS_W = {
    "bus2":  (60,  290, 360, 55, "busLock  总线事务锁 [lock/TryEnter]"),
    "queue": (60,  350, 360, 75, "writeQueue      [ConcurrentQueue]"),
    "aw":    (460, 350, 360, 75, "activeWrite     [volatile]"),
    "sp2":   (860, 350, 360, 75, "sp 串口         [lock(io)]"),
    "wr1":   (60,  435, 360, 75, "wr1.Completed   [ManualResetEvent]"),
    "wr2":   (460, 435, 360, 75, "wr2.Completed   [ManualResetEvent]"),
    "ack":   (860, 435, 360, 75, "wr.Ack          [AutoResetEvent]"),
}
P_BTN_A = (150, 85); P_BTN_B = (150, 130)
P_QUEUE = (240, 395); P_WL = (640, 395); P_AW = (1040, 395)
P_EX_W = (640, 480); P_SP2 = (240, 480); P_HD_W = (1040, 480)
P_BG_W = (150, 220); P_WR1 = (240, 480); P_WR2 = (640, 480)

def states_w(t):
    btn = ("run", "RUN Enqueue")
    if t < 0.1: btn = ("idle", "idle")
    elif t >= 3.0: btn = ("idle", "已提交，继续接收")

    wl = ("idle", "idle 空队列")
    if 4.5 <= t < 5.5: wl = ("run", "RUN TryDequeue wr1")
    elif 5.5 <= t < 8.0: wl = ("run", "RUN ExecuteWrite(wr1)")
    elif 9.5 <= t < 11.0: wl = ("run", "RUN ExecuteWrite(wr2)")
    elif 8.0 <= t < 9.5: wl = ("run", "RUN 取 wr2")

    ex = ("idle", "idle")
    if 5.5 <= t < 7.0: ex = ("wait", "WAIT wr1.Ack")
    elif 7.0 <= t < 8.0: ex = ("run", "RUN wr1完成")
    elif 9.5 <= t < 11.0: ex = ("run", "RUN wr2执行中")

    bg = ("wait", "WAIT Completed")
    if 8.0 <= t < 9.5: bg = ("run", "RUN 打印wr1结果")
    elif 11.0 <= t < 12.5: bg = ("run", "RUN 打印wr2结果")

    # SendLoop 读线程：写流程期间 TryEnter(busLock) 反复失败跳过
    # 只有 WriteLoop 释放 busLock 的间隙（8.0~9.5 之间一小段）能成功一轮
    tx2 = ("wait", "WAIT TryEnter 失败跳过")
    if 8.0 <= t < 9.5: tx2 = ("run", "RUN TryEnter 成功读一轮")
    elif t < 4.3: tx2 = ("run", "RUN 读轮询(无写)")
    return {"BTN": btn, "WL": wl, "EX": ex, "BG": bg, "TX2": tx2}

def draw_lanes_w(d, t, st):
    for key, name, c, y in LANES_W:
        mode, txt = st[key]
        d.text((40, y + 6), name, font=f_lbl, fill=c)
        bg = C_WAIT if mode == "wait" else C_BOX
        d.rounded_rectangle([200, y, 1090, y + 30], 6, fill=bg, outline=C_EDGE)
        if mode == "run":
            spd = {"WL": 200, "EX": 150, "BTN": 100, "BG": 80, "TX2": 180}[key]
            x = 210 + ((t * spd) % 860)
            d.ellipse([x - 6, y + 9, x + 6, y + 21], fill=c)
        elif mode == "wait":
            d.ellipse([206, y + 9, 218, y + 21], fill=C_RED)
            if int(t * 3) % 2 == 0:
                # TX2 的 wait 是 TryEnter 失败跳过，不是阻塞
                wait_txt = "TryEnter 失败跳过…" if key == "TX2" else "阻塞中…"
                d.text((230, y + 7), wait_txt, font=f_small, fill=C_RED)
        tc = {"run": c, "wait": C_RED, "idle": C_GRAY}[mode]
        d.text((1100, y + 7), txt, font=f_small, fill=tc)

def draw_shared_w(d, t):
    d.rounded_rectangle([30, 280, 1250, 490], 10, outline=(40, 60, 90), width=2)
    d.text((45, 288), "共享内存 / 临界区（写流程）", font=f_lbl, fill=C_LBL)
    for key, (x, y, w, h, title) in CELLS_W.items():
        d.rounded_rectangle([x, y, x + w, y + h], 8, fill=C_BOX, outline=C_EDGE, width=2)
        d.text((x + 12, y + 8), title, font=f_lbl, fill=C_TITLE)
        if key == "bus2":
            # 写流程 busLock 持有者：WriteLoop(4.3~8.0, 9.5~11.0) / SendLoop(8.0~9.5) / 空闲
            if 4.3 <= t < 8.0 or 9.5 <= t < 11.0:
                owner, txt, c = "WL", "WriteLoop 持有(lock 阻塞等)", C_WL
            elif 8.0 <= t < 9.5:
                owner, txt, c = "TX2", "SendLoop 持有(TryEnter 成功)", C_TX
            else:
                owner, txt, c = None, "空闲", C_GRAY
            draw_lock(d, x + w - 40, y + 22, owner if owner else None)
            # draw_lock 用 OWNER_C 映射，WL/TX2 不在 OWNER_C 里，手动传色
            # 改用直接画锁：owner 颜色
            d.text((x + 12, y + 30), txt, font=f_small, fill=c)
        elif key == "queue":
            n = 0 if t < 0.5 else 1 if t < 2.0 else 2 if t < 9.5 else 1 if t < 11.0 else 0
            for i in range(4):
                cx = x + 14 + i * 40
                on = i < n
                d.rectangle([cx, y + 38, cx + 34, y + 58],
                            fill=C_ON if on else C_CELL, outline=C_ONED if on else C_CELED)
            d.text((x + 180, y + 42), f"[{n}个待执行]", font=f_small, fill=C_LBL)
        elif key == "aw":
            v = "null"
            if 4.5 <= t < 9.5: v = "wr1 @ 0x1000"
            elif 9.5 <= t < 11.5: v = "wr2 @ 0x2000"
            cv = C_ACT if (4.5 <= t < 5.0 or 9.5 <= t < 10.0) else C_TITLE
            d.text((x + 12, y + 38), f"= {v}", font=f_log, fill=cv)
        elif key == "sp2":
            on = (5.5 <= t < 7.0) or (9.5 <= t < 11.0)
            d.text((x + 12, y + 38), "COM4 写帧中" if on else "空闲", font=f_log,
                   fill=C_ACT if on else C_GRAY)
        elif key == "wr1":
            on = 8.0 <= t < 12.5
            d.ellipse([x + 14, y + 38, x + 34, y + 58], fill=(60, 220, 100) if on else (200, 60, 60))
            d.text((x + 44, y + 40), "Set(成功)" if on else "未置位", font=f_small,
                   fill=C_LOG if on else C_RED)
        elif key == "wr2":
            on = 11.0 <= t < 12.5
            d.ellipse([x + 14, y + 38, x + 34, y + 58], fill=(60, 220, 100) if on else (200, 60, 60))
            d.text((x + 44, y + 40), "Set(成功)" if on else "未置位", font=f_small,
                   fill=C_LOG if on else C_RED)
        elif key == "ack":
            on = (7.0 <= t < 7.5) or (10.5 <= t < 11.0)
            d.ellipse([x + 14, y + 38, x + 34, y + 58], fill=(60, 220, 100) if on else (200, 60, 60))
            d.text((x + 44, y + 40), "Ack.Set" if on else "未置位", font=f_small,
                   fill=C_LOG if on else C_RED)

def draw_dots_w(d, t):
    dot(d, lerp(P_BTN_A, P_QUEUE, 0.2, 1.4, t), 7, C_BTN)      # ① 按钮A→队列
    dot(d, lerp(P_BTN_B, P_QUEUE, 1.7, 2.9, t), 7, C_BTN)      # ② 按钮B→队列
    dot(d, lerp(P_QUEUE, P_WL, 4.5, 5.4, t), 8, C_WL)          # ④ 队列→WriteLoop
    dot(d, lerp(P_WL, P_EX_W, 5.5, 6.5, t), 8, C_WL)           # ⑤ WriteLoop→ExecuteWrite
    dot(d, lerp(P_EX_W, P_SP2, 6.5, 7.0, t), 8, C_EX)          #    →串口
    dot(d, lerp(P_SP2, P_HD_W, 7.0, 7.5, t), 8, C_EX)          # ⑥ 串口→Handle
    dot(d, lerp(P_HD_W, P_EX_W, 7.5, 8.0, t), 8, C_BG2)        # ⑦ Handle→ExecuteWrite(Ack)
    dot(d, lerp(P_EX_W, P_BG_W, 8.0, 9.0, t), 8, C_BG2)        #    Completed→后台线程
    dot(d, lerp(P_QUEUE, P_WL, 9.5, 10.0, t), 8, C_WL)         # ⑧ 取wr2
    dot(d, lerp(P_WL, P_EX_W, 10.0, 10.8, t), 8, C_WL)
    dot(d, lerp(P_EX_W, P_BG_W, 11.0, 12.0, t), 8, C_BG2)      # ⑨ wr2完成→后台线程B

def draw_misc_w(d, t):
    label = ""
    for ts, s in STEPS_W:
        if t >= ts: label = s
    d.text((40, 38), label, font=f_step, fill=C_ACT)
    if t >= 8.0:
        d.text((60, 545), "[结果] 从站01 线圈0 ON 执行成功 ✓ (wr1.Completed 唤醒后台线程A)", font=f_log, fill=C_LOG)
    if t >= 11.0:
        d.text((60, 568), "[结果] 从站02 线圈0 OFF 执行成功 ✓ (wr2.Completed 唤醒后台线程B)", font=f_log, fill=C_LOG)
    if t >= 12.5:
        d.text((60, 591), "关键：每个 wr 独立 Completed，A 完成只唤醒等 A 的，不会错配", font=f_log, fill=C_ACT)
    # 图例
    ly = 630
    d.text((60, ly), "图例：", font=f_lbl, fill=C_LBL)
    for i, (name, c) in enumerate([("按钮/CmdLoop", C_BTN), ("WriteLoop", C_WL),
                                   ("ExecuteWrite", C_EX), ("后台打印线程", C_BG2),
                                   ("SendLoop 读", C_TX)]):
        x = 150 + i * 150
        d.ellipse([x, ly + 4, x + 12, ly + 16], fill=c)
        d.text((x + 18, ly + 2), name, font=f_small, fill=C_LBL)
    d.text((60, ly + 28), "busLock：WriteLoop lock 死等(写优先) / SendLoop TryEnter 失败跳过(读让步) / 间隙读恢复一轮",
           font=f_small, fill=C_GRAY)

def draw_subtitle_w(d, t):
    sub = ""
    for ts, s in SUBS_W:
        if t >= ts: sub = s
    d.rounded_rectangle([110, 1015, 1170, 1085], 10, fill=(8, 10, 15), outline=(60, 70, 90), width=2)
    d.text((128, 1042), f"t={t:04.1f}s", font=f_lbl, fill=C_ACT)
    w = d.textlength(sub, font=f_sub)
    d.text(((W - w) / 2 + 40, 1038), sub, font=f_sub, fill=(245, 245, 245))

def draw_write_frame(d, t):
    d.text((40, 10), "多按钮写操作 · 队列+独立信号异步通信（改造后）", font=f_title, fill=C_TITLE)
    st = states_w(t)
    draw_lanes_w(d, t, st)
    draw_shared_w(d, t)
    draw_dots_w(d, t)
    draw_misc_w(d, t)
    draw_subtitle_w(d, t)

def main():
    mp4 = imageio.get_writer(r"F:\chenbo\dataflow.mp4", fps=FPS,
                             codec="libx264", quality=8, macro_block_size=None)
    gifs = []
    for i in range(N):
        img = make_frame(i / FPS)
        mp4.append_data(np.asarray(img))
        if i % 2 == 0:
            gifs.append(img.resize((W * 3 // 4, H * 3 // 4), Image.LANCZOS))
        if i % 30 == 0:
            print(f"frame {i}/{N}")
    mp4.close()
    imageio.mimsave(r"F:\chenbo\dataflow.gif", gifs, duration=2 / FPS, loop=0)
    print("DONE")

if __name__ == "__main__":
    main()

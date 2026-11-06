"""
复刻 dataflow.html 的两个 SVG 动画，生成 dataflow.mp4
动画1：Modbus RTU 多从站轮询数据流转（12s 循环）
动画2：多按钮写操作 队列+独立信号异步通信（14s 循环）
合成：动画1 + 间隔 + 动画2，总 30s，30fps，1000x640
"""
import cv2
import numpy as np
import math

W, H = 1000, 640
FPS = 30
DURATION1 = 12.0   # 动画1时长
GAP = 2.0          # 两动画间隔
DURATION2 = 14.0   # 动画2时长
TOTAL = DURATION1 + GAP + DURATION2

# 颜色（BGR）
C_BG = (32, 20, 15)       # #0f1420
C_BOX = (51, 74, 58)      # #1a2333 边框
C_BOX_BORDER = (99, 74, 58)  # #3a4a63
C_ACTIVE = (74, 213, 255)    # #ffd54a
C_LBL = (209, 179, 159)      # #9fb3d1
C_BIG = (247, 238, 232)      # #e8eef7
C_MSG = (74, 213, 255)       # #ffd54a
C_LOG = (160, 211, 143)      # #8fd3a0
C_CELL = (39, 24, 16)        # #101827
C_CELL_BORDER = (92, 65, 51) # #33415c
C_CELL_ON = (255, 134, 46)   # #2e86ff
C_GAP_BAR = (77, 183, 255)   # #ffb74d
C_P0 = (0, 252, 124)         # #7CFC00
C_P1 = (255, 178, 77)        # #4db2ff
C_P4 = (74, 213, 255)        # #ffd54a
C_P5 = (176, 125, 255)       # #ff7db0

# 中文字体（用 SimHei 或 Microsoft YaHei）
import os
font_path = r"C:\Windows\Fonts\msyh.ttc"
if not os.path.exists(font_path):
    font_path = r"C:\Windows\Fonts\simhei.ttf"

from PIL import Image, ImageDraw, ImageFont
font_big = ImageFont.truetype(font_path, 18)
font_mid = ImageFont.truetype(font_path, 14)
font_sml = ImageFont.truetype(font_path, 13)
font_log = ImageFont.truetype(font_path, 13)

def pil_to_cv(img):
    return cv2.cvtColor(np.array(img), cv2.COLOR_RGB2BGR)

def cv_to_pil(img):
    return Image.fromarray(cv2.cvtColor(img, cv2.COLOR_BGR2RGB))

def draw_text(img, text, x, y, font, color):
    """在 cv 图上用 PIL 画文字（支持中文）"""
    pil = cv_to_pil(img)
    draw = ImageDraw.Draw(pil)
    # color 是 BGR，转 RGB
    rgb = (color[2], color[1], color[0])
    draw.text((x, y), text, font=font, fill=rgb)
    return pil_to_cv(pil)

def draw_rect(img, x, y, w, h, color, border=-1, border_color=None, radius=0):
    """画矩形，可选圆角、填充或边框"""
    if border == -1:  # 填充
        cv2.rectangle(img, (x, y), (x+w, y+h), color, -1)
    else:
        cv2.rectangle(img, (x, y), (x+w, y+h), color, border)
    if border_color is not None and border > 0:
        cv2.rectangle(img, (x, y), (x+w, y+h), border_color, border)

def draw_box(img, x, y, w, h, active=False):
    """画模块框"""
    border_color = C_ACTIVE if active else C_BOX_BORDER
    thickness = 3 if active else 2
    cv2.rectangle(img, (x, y), (x+w, y+h), C_BOX, -1)
    cv2.rectangle(img, (x, y), (x+w, y+h), border_color, thickness)

def lerp(a, b, k):
    return (int(a[0] + (b[0]-a[0])*k), int(a[1] + (b[1]-a[1])*k))

def move_dot(img, dot, frm, to, t0, t1, now, radius=7):
    if now < t0 or now > t1:
        return
    k = (now - t0) / (t1 - t0)
    cx, cy = lerp(frm, to, k)
    cv2.circle(img, (cx, cy), radius, dot, -1)

# ===== 动画1：Modbus RTU 多从站轮询 =====
P1 = {
    'tx': (150, 105), 'sp': (500, 75), 'rx': (840, 105),
    'buf': (500, 278), 'bufIn': (620, 278),
    'fx': (160, 265), 'dr': (500, 445)
}

steps1 = [
    (0,    "① 发送线程：curSlave=0x01，lock(io) 发出 8 字节请求帧 01 03 00 00 00 02 CRC"),
    (1500, "② 从站应答：字节分 3 批经串口流入 OnData"),
    (3000, "③ OnData：lock(gate) 追加 buf，Interlocked 记录最后接收时刻"),
    (4800, "④ 字节流停：FrameGapLoop 检测静默 ≥ 3.5 字符(3.65ms) → 判帧结束"),
    (6200, "⑤ Drain：按 FrameLength 切走完整帧（03 应答 = 3+字节数+2 = 9 字节）"),
    (7600, "⑥ Handle：CRC16 校验通过，frame[0]==curSlave 匹配，解析温湿度"),
    (9000, "⑦ frameReceived.Set() → 唤醒发送线程，idx 切到从站 0x02，发下一帧"),
    (10600, "—— 一轮完成，循环 ——"),
]

def render_frame1(now_ms):
    img = np.full((H, W, 3), C_BG, dtype=np.uint8)
    # 标题
    img = draw_text(img, "Modbus RTU 多从站轮询 · 数据流转动态演示", 40, 10, font_big, C_BIG)

    # 模块框
    active_tx = now_ms < 1500 or (9000 <= now_ms < 10600)
    active_sp = now_ms < 3000
    active_rx = 1500 <= now_ms < 4800
    active_fx = 4800 <= now_ms < 6200
    active_dr = 6200 <= now_ms < 9000

    draw_box(img, 40, 60, 220, 90, active_tx)
    img = draw_text(img, "SendLoop 发送线程", 60, 75, font_mid, C_BIG)
    img = draw_text(img, "轮询从站 0x01 / 0x02", 50, 100, font_sml, C_LBL)
    img = draw_text(img, "lock(io) 写串口 → 等信号 50ms", 50, 120, font_sml, C_LBL)

    draw_box(img, 400, 40, 200, 70, active_sp)
    img = draw_text(img, "串口 COM4", 430, 55, font_mid, C_BIG)
    img = draw_text(img, "RS485 半双工 9600bps", 420, 80, font_sml, C_LBL)

    draw_box(img, 720, 60, 240, 90, active_rx)
    img = draw_text(img, "OnData 接收线程", 740, 75, font_mid, C_BIG)
    img = draw_text(img, "lock(io) 读字节", 725, 100, font_sml, C_LBL)
    img = draw_text(img, "lock(gate) 追加 buf", 725, 120, font_sml, C_LBL)

    # buf 缓冲区
    draw_box(img, 380, 220, 240, 110, False)
    img = draw_text(img, "buf 缓冲区 (gate)", 420, 235, font_mid, C_BIG)
    # 8 个 cell
    fill_n = 0
    if now_ms < 3100: fill_n = 0
    elif now_ms < 3500: fill_n = 3
    elif now_ms < 3900: fill_n = 6
    elif now_ms < 6200: fill_n = 9
    else: fill_n = 0
    for i in range(8):
        cx = 400 + i*27
        cy = 265
        col = C_CELL_ON if i < fill_n else C_CELL
        cv2.rectangle(img, (cx, cy), (cx+22, cy+26), col, -1)
        cv2.rectangle(img, (cx, cy), (cx+22, cy+26), C_CELL_BORDER, 1)

    # FrameGapLoop
    draw_box(img, 40, 220, 240, 90, active_fx)
    img = draw_text(img, "FrameGapLoop 检测线程", 55, 235, font_mid, C_BIG)
    img = draw_text(img, "Stopwatch 微秒轮询", 50, 260, font_sml, C_LBL)
    img = draw_text(img, "> 3.5字符(3.65ms)判帧尾", 50, 280, font_sml, C_LBL)

    # Drain/Handle
    draw_box(img, 380, 400, 240, 90, active_dr)
    img = draw_text(img, "Drain / Handle", 430, 415, font_mid, C_BIG)
    img = draw_text(img, "切帧 → CRC16 → 比对 curSlave", 390, 440, font_sml, C_LBL)
    img = draw_text(img, "解析温湿度 → Set 信号", 390, 460, font_sml, C_LBL)

    # 静默计时条
    img = draw_text(img, "帧间静默计时：", 650, 225, font_sml, C_LBL)
    cv2.rectangle(img, (650, 240), (850, 252), C_CELL, -1)
    cv2.rectangle(img, (650, 240), (850, 252), C_CELL_BORDER, 1)
    g = int((now_ms - 4800) / 1400 * 200) if 4800 <= now_ms < 6200 else 0
    if g > 0:
        cv2.rectangle(img, (650, 240), (650+g, 252), C_GAP_BAR, -1)

    # 步骤文字
    label = ""
    for t, s in steps1:
        if now_ms >= t:
            label = s
    img = draw_text(img, label, 40, 30, font_mid, C_MSG)

    # 移动数据点
    move_dot(img, C_P0, P1['tx'], P1['sp'], 200, 1400, now_ms, 7)
    move_dot(img, C_P1, P1['sp'], P1['rx'], 1600, 2400, now_ms, 6)
    move_dot(img, C_P1, P1['sp'], P1['rx'], 2100, 2900, now_ms, 6)
    move_dot(img, C_P1, P1['sp'], P1['rx'], 2500, 3000, now_ms, 6)
    move_dot(img, C_P1, P1['rx'], P1['bufIn'], 3100, 3800, now_ms, 6)
    move_dot(img, C_P1, P1['rx'], P1['bufIn'], 3500, 4200, now_ms, 6)
    move_dot(img, C_P1, P1['rx'], P1['bufIn'], 3900, 4600, now_ms, 6)
    move_dot(img, C_P4, P1['fx'], P1['buf'], 5000, 6100, now_ms, 8)
    move_dot(img, C_P4, P1['buf'], P1['dr'], 6300, 7500, now_ms, 8)
    move_dot(img, C_P5, P1['dr'], P1['tx'], 9000, 10500, now_ms, 8)

    # 解析输出
    if now_ms >= 7600:
        img = draw_text(img, "[解析·从站01] 共2个寄存器：[0]=253 [1]=605", 40, 560, font_log, C_LOG)
    if now_ms >= 8300:
        img = draw_text(img, "[温湿度·从站01] 温度 25.3 ℃，湿度 60.5 %RH   CRC OK", 40, 585, font_log, C_LOG)

    return img

# ===== 动画2：多按钮写操作 =====
Q = {
    'btnA': (140, 95), 'btnB': (140, 185), 'queue': (400, 182), 'wl': (670, 125),
    'ex': (670, 265), 'sp2': (895, 185), 'hd': (670, 400), 'bg': (150, 400)
}

steps2 = [
    (0,    "① 按钮A：new wr1{Completed=信号C1} → writeQueue.Enqueue(wr1) → 立即返回'已提交'"),
    (1500, "② 按钮B：new wr2{Completed=信号C2} → writeQueue.Enqueue(wr2) → 立即返回（不阻塞）"),
    (3000, "③ 后台线程A/B 各自 wr.Completed.Wait（闭包捕获自己的 wr，互不干扰）"),
    (4500, "④ WriteLoop：TryDequeue 取出 wr1 → ExecuteWrite(wr1) → activeWrite=wr1"),
    (5500, "⑤ ExecuteWrite：lock(io) sp.Write(wr1.Frame) → wr1.Ack.WaitOne 等回显"),
    (7000, "⑥ Handle 收到 0x05 回显 → 比对 activeWrite.Frame → aw.AckOk=true → aw.Ack.Set()"),
    (8000, "⑦ ExecuteWrite 被唤醒 → wr1.Result=true → wr1.Completed.Set() → 唤醒后台线程A"),
    (9500, "⑧ WriteLoop 取 wr2 → ExecuteWrite(wr2) → activeWrite=wr2（串行，不会与wr1冲突）"),
    (11000, "⑨ wr2 完成 → wr2.Completed.Set() → 唤醒后台线程B（各等各的，不会错配）"),
    (12500, "—— 多按钮并发提交、串行执行、独立回传完成 ——"),
]

def render_frame2(now_ms):
    img = np.full((H, W, 3), C_BG, dtype=np.uint8)
    img = draw_text(img, "多按钮写操作 · 队列+独立信号异步通信（改造后）", 40, 10, font_big, C_BIG)

    active_q = 0 <= now_ms < 4500
    active_wl = 4500 <= now_ms < 12500
    active_ex = (5000 <= now_ms < 9000) or (9500 <= now_ms < 12000)
    active_sp2 = (5500 <= now_ms < 7000) or (10000 <= now_ms < 11500)
    active_hd = (6500 <= now_ms < 8000) or (11000 <= now_ms < 12000)
    active_bg = 3000 <= now_ms < 12500

    # 按钮A/B
    draw_box(img, 40, 60, 200, 70, False)
    img = draw_text(img, "按钮A / CmdLoop", 55, 75, font_mid, C_BIG)
    img = draw_text(img, "new wr1 → Enqueue → 立即返回", 50, 100, font_sml, C_LBL)

    draw_box(img, 40, 150, 200, 70, False)
    img = draw_text(img, "按钮B / CmdLoop", 55, 165, font_mid, C_BIG)
    img = draw_text(img, "new wr2 → Enqueue → 立即返回", 50, 190, font_sml, C_LBL)

    # writeQueue
    draw_box(img, 300, 100, 200, 120, active_q)
    img = draw_text(img, "writeQueue", 325, 115, font_mid, C_BIG)
    img = draw_text(img, "ConcurrentQueue", 310, 140, font_sml, C_LBL)
    # 4 个队列 cell
    fill_q = 0
    if now_ms < 1500: fill_q = 0
    elif now_ms < 3000: fill_q = 1
    elif now_ms < 9500: fill_q = 2
    elif now_ms < 11000: fill_q = 1
    else: fill_q = 0
    for i in range(4):
        cx = 315 + i*42
        cy = 170
        col = C_CELL_ON if i < fill_q else C_CELL
        cv2.rectangle(img, (cx, cy), (cx+36, cy+24), col, -1)
        cv2.rectangle(img, (cx, cy), (cx+36, cy+24), C_CELL_BORDER, 1)
    img = draw_text(img, "多线程无锁 Enqueue", 310, 205, font_sml, C_LBL)

    # WriteLoop
    draw_box(img, 560, 80, 220, 90, active_wl)
    img = draw_text(img, "WriteLoop 写线程", 575, 95, font_mid, C_BIG)
    img = draw_text(img, "TryDequeue → 串行执行", 565, 120, font_sml, C_LBL)
    img = draw_text(img, "保证 RS485 一次一帧", 565, 140, font_sml, C_LBL)

    # ExecuteWrite
    draw_box(img, 560, 220, 220, 90, active_ex)
    img = draw_text(img, "ExecuteWrite", 575, 235, font_mid, C_BIG)
    img = draw_text(img, "activeWrite=wr → sp.Write", 565, 260, font_sml, C_LBL)
    img = draw_text(img, "wr.Ack.WaitOne 等回显", 565, 280, font_sml, C_LBL)

    # 串口
    draw_box(img, 820, 150, 150, 70, active_sp2)
    img = draw_text(img, "串口 COM4", 835, 165, font_mid, C_BIG)
    img = draw_text(img, "lock(io) 写", 830, 190, font_sml, C_LBL)

    # Handle
    draw_box(img, 560, 360, 220, 80, active_hd)
    img = draw_text(img, "Handle 接收", 585, 375, font_mid, C_BIG)
    img = draw_text(img, "收到0x05回显 → 比对 activeWrite", 565, 400, font_sml, C_LBL)
    img = draw_text(img, "aw.AckOk=true → aw.Ack.Set()", 565, 420, font_sml, C_LBL)

    # 后台打印
    draw_box(img, 40, 360, 220, 80, active_bg)
    img = draw_text(img, "后台打印线程", 55, 375, font_mid, C_BIG)
    img = draw_text(img, "ThreadPool 回调", 50, 400, font_sml, C_LBL)
    img = draw_text(img, "wr.Completed.Wait → 打印结果", 50, 420, font_sml, C_LBL)

    # 独立信号说明
    draw_box(img, 300, 360, 220, 80, False)
    img = draw_text(img, "每个 wr 独立信号", 325, 375, font_mid, C_BIG)
    img = draw_text(img, "Ack: Handle→ExecuteWrite", 310, 400, font_sml, C_LBL)
    img = draw_text(img, "Completed: WriteLoop→提交方", 310, 420, font_sml, C_LBL)

    # 步骤文字
    label = ""
    for t, s in steps2:
        if now_ms >= t:
            label = s
    img = draw_text(img, label, 40, 30, font_mid, C_MSG)

    # 移动数据点
    move_dot(img, C_P0, Q['btnA'], Q['queue'], 200, 1400, now_ms, 7)
    move_dot(img, C_P1, Q['btnB'], Q['queue'], 1700, 2900, now_ms, 7)
    move_dot(img, C_P4, Q['queue'], Q['wl'], 4500, 5400, now_ms, 8)
    move_dot(img, C_P4, Q['wl'], Q['ex'], 5500, 6500, now_ms, 8)
    move_dot(img, C_P5, Q['ex'], Q['sp2'], 6500, 7500, now_ms, 8)
    move_dot(img, C_P5, Q['sp2'], Q['hd'], 7000, 7800, now_ms, 8)
    move_dot(img, C_P0, Q['hd'], Q['ex'], 8000, 8500, now_ms, 8)
    move_dot(img, C_P0, Q['ex'], Q['bg'], 8500, 9400, now_ms, 8)
    move_dot(img, C_P4, Q['queue'], Q['wl'], 9500, 10000, now_ms, 8)
    move_dot(img, C_P5, Q['wl'], Q['ex'], 10000, 10800, now_ms, 8)
    move_dot(img, C_P0, Q['ex'], Q['bg'], 11000, 12000, now_ms, 8)

    # 输出
    if now_ms >= 8000:
        img = draw_text(img, "[结果] 从站01 线圈0 ON 执行成功 ✓ (wr1.Completed 唤醒后台线程A)", 40, 480, font_log, C_LOG)
    if now_ms >= 11000:
        img = draw_text(img, "[结果] 从站02 线圈0 OFF 执行成功 ✓ (wr2.Completed 唤醒后台线程B)", 40, 505, font_log, C_LOG)
    if now_ms >= 12500:
        img = draw_text(img, "关键：每个 wr 独立 Completed 信号，A 完成只唤醒等 A 的，不会错配", 40, 530, font_log, C_LOG)

    return img

# ===== 生成视频 =====
out_path = r"F:\chenbo\dataflow_new.mp4"
fourcc = cv2.VideoWriter_fourcc(*'mp4v')
writer = cv2.VideoWriter(out_path, fourcc, FPS, (W, H))

total_frames = int(TOTAL * FPS)
print(f"总帧数: {total_frames}, 总时长: {TOTAL}s")

for i in range(total_frames):
    t = i / FPS
    if t < DURATION1:
        now_ms = (t % DURATION1) * 1000
        frame = render_frame1(now_ms)
    elif t < DURATION1 + GAP:
        # 间隔黑帧 + 提示
        frame = np.full((H, W, 3), C_BG, dtype=np.uint8)
        frame = draw_text(frame, "下一节：多按钮写操作 · 队列+独立信号异步通信", 200, H//2, font_big, C_BIG)
    else:
        t2 = t - DURATION1 - GAP
        now_ms = (t2 % DURATION2) * 1000
        frame = render_frame2(now_ms)
    writer.write(frame)
    if i % 30 == 0:
        print(f"  进度: {i}/{total_frames} ({i/total_frames*100:.0f}%)")

writer.release()
print(f"\n视频已生成: {out_path}")
print(f"文件大小: {os.path.getsize(out_path)/1024:.0f} KB")

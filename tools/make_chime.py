"""
生成 CutTimer 的默认提醒音（一个干净的两音铃声）。

为什么自己合成而不是从网上下：
  - 免费音效站基本都要登录才能下载（directory.audio / freesound 都是），
    能直接下的大多版权不明，不适合往工具里塞；
  - 自己合成没有任何版权问题，长度和音色完全可控 —— 循环播放时这一点很重要。

音色做法（类似马林巴/音叉）：
  - 两个音：C6 (1046.5 Hz) 起，G6 (1568.0 Hz) 跟上，构成一个上行的纯五度，听起来是「向上」的
  - 每个音由基频 + 少量高次泛音叠加，泛音衰减更快，所以起音亮、尾音纯
  - 指数衰减包络 + 5ms 淡入（避免爆音）
  - 末尾做淡出，循环播放时接缝不会「咔」一声

用法：python tools/make_chime.py <输出路径>
"""

import math
import struct
import sys
import wave

RATE = 44100
DURATION = 1.7          # 秒，够短，循环时不会拖沓
PEAK = 0.62             # 留足余量，避免叠加后削波

# (起始时间秒, 基频 Hz, 振幅)
NOTES = [
    (0.00, 1046.50, 1.00),   # C6
    (0.17, 1567.98, 0.85),   # G6
]

# (倍频, 相对振幅, 衰减倍率) —— 高次泛音衰得快，音色更像真实乐器
PARTIALS = [
    (1.0, 1.00, 1.0),
    (2.0, 0.32, 1.6),
    (3.0, 0.14, 2.2),
    (4.0, 0.06, 3.0),
]


def main(out_path: str) -> None:
    n = int(RATE * DURATION)
    buf = [0.0] * n

    for start, freq, amp in NOTES:
        i0 = int(start * RATE)
        for i in range(i0, n):
            t = (i - i0) / RATE
            if t < 0:
                continue
            # 指数衰减，1.1 秒的时间常数
            env = math.exp(-t / 0.42)
            # 5ms 淡入，避免起音爆音
            if t < 0.005:
                env *= t / 0.005

            s = 0.0
            for mult, pamp, pdecay in PARTIALS:
                s += pamp * math.exp(-t / (0.42 / pdecay)) * math.sin(2 * math.pi * freq * mult * t)
            buf[i] += amp * env * s

    # 末尾 120ms 淡出，循环接缝不「咔」
    fade = int(0.12 * RATE)
    for k in range(fade):
        idx = n - fade + k
        if 0 <= idx < n:
            buf[idx] *= 1.0 - (k / fade)

    peak = max(abs(v) for v in buf) or 1.0
    scale = PEAK / peak
    frames = b"".join(struct.pack("<h", int(max(-1.0, min(1.0, v * scale)) * 32767)) for v in buf)

    with wave.open(out_path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(frames)

    print(f"wrote {out_path}  {DURATION}s  {len(frames)} bytes")


if __name__ == "__main__":
    out = sys.argv[1] if len(sys.argv) > 1 else "chime.wav"
    main(out)

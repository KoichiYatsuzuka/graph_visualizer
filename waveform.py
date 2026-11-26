"""waveform — 電極に加える電位の波形を、Arrow の 1 フレーム分まとめて計算する（main.ar が import[py] で呼ぶ）。

関数:
    sine_potentials(center, amplitude, start_phase_cycles, phase_step_cycles, count) -> list[float]
        sin 波の電位を count 点まとめて返す。
"""
import math


def sine_potentials(
    center: float,
    amplitude: float,
    start_phase_cycles: float,
    phase_step_cycles: float,
    count: int,
) -> list[float]:
    """sin 波の電位を、位相を等間隔に進めながら count 点まとめて計算する。

    Arrow から 1 フレームに 1 回だけ呼び、時間発展の小刻み（サブステップ）の分の sin をまとめて計算させるための関数。

    Args:
        center: sin 波の中心電位 [V]。
        amplitude: sin 波の振幅 [V]。
        start_phase_cycles: 最初の点の位相 [周期]（1.0 で 1 周期 = 2π）。
        phase_step_cycles: 隣り合う点の位相の差 [周期]。
        count: 計算する点の数（0 以上）。

    Returns:
        index = 0, 1, ..., count - 1 について
        center + amplitude * sin(2π (start_phase_cycles + index * phase_step_cycles)) を並べた list。

    Raises:
        ValueError: count が負のとき。

    Example:
        >>> sine_potentials(0.0, 0.01, 0.25, 0.25, 2)   # 位相 90°, 180°
        [0.01, 1.2246467991473532e-18]
    """
    if count < 0:
        raise ValueError(f"count must be 0 or more, got {count}")
    return [
        center + amplitude * math.sin(2.0 * math.pi * (start_phase_cycles + index * phase_step_cycles))
        for index in range(count)
    ]

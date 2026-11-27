using System;

namespace TimeSeriesPlotBridge;

// SpectrumPlot — スペクトルの経時変化を描くための WinForms の窓（ScottPlot の FormsPlot が 1 枚）。Arrow から import[cs-dll] で使う
// （Arrow の型スタブに出るクラス。もう 1 つは TimeSeriesPlot）
//
// スペクトルを 1 本の線で描く（横軸の波長は SetWavelengths で 1 回、縦軸の吸光度は SetAbsorbances で毎フレーム）。
// 窓は専用の STA スレッドで動く（UiThreadHost）。Arrow のメインスレッドは SetAbsorbances で値の写しを書き換えるだけで
// UI スレッドを待たず、UI スレッドが RedrawInterval ごとに、書き換えがあれば描き直す。
// Arrow から使うときは time_series_plot/__init__.ar の SpectrumWindow を通す。
//
// メンバー:
//   _host: UiThreadHost<SpectrumPlotForm> — 窓と、その UI スレッド（UI スレッドの例外の記録も持つ）
//   _stateLock: object           — _wavelengths / _absorbances / _hasUnrenderedChange を守る lock
//   _wavelengths, _absorbances: double[] — スペクトルの写し（Arrow のスレッドが書き、UI スレッドが読む。最初は 0 点）
//   _hasUnrenderedChange: bool   — まだ描いていない書き換えがあるか
//   SpectrumPlot(title, xLabel, yLabel, visible) — 窓を作る（visible が false なら表示しない・テスト用）
//   SetAxisLimits(xMin, xMax, yMin, yMax) — 軸の範囲を決める（ダブルクリック・中クリックでこの範囲に戻る）
//   SetWavelengths(wavelengths)  — 横軸の点を決める（吸光度は 0 に戻す）
//   SetAbsorbances(absorbances)  — 縦軸の値を書き換える（待たずに戻る）
//   PointCount(), WavelengthAt(index), AbsorbanceAt(index) — 写しの値（テスト用）
//   RenderNow()                  — 今すぐ描き直す（描き終わるまで待つ）
//   SavePng(path, width, height) — 今の図を PNG に保存する（描き終わるまで待つ）
//   IsOpen() -> bool, Close()    — 窓が開いているか・窓を閉じる
//
// 例（Arrow から・ふつうは SpectrumWindow を使う）:
//   import[cs-dll] .TimeSeriesPlotBridge as bridge
//   let spectrum = bridge.SpectrumPlot("graph_visualizer spectrum", "Wavelength / nm", "Absorbance", True)
//   spectrum.SetAxisLimits(200.0, 800.0, 0.0, 1.0)
//   spectrum.SetWavelengths([200.0, 202.0, 204.0])
//   spectrum.SetAbsorbances([0.0, 0.1, 0.0])
public sealed class SpectrumPlot
{
    private readonly UiThreadHost<SpectrumPlotForm> _host;
    private readonly object _stateLock = new();
    private double[] _wavelengths = Array.Empty<double>();
    private double[] _absorbances = Array.Empty<double>();
    private bool _hasUnrenderedChange;

    // SpectrumPlot — 専用の STA スレッドで空のグラフの窓を作り、窓ができるまで待って戻る
    //   title:  窓のタイトル
    //   xLabel: 横軸の名前、yLabel: 縦軸の名前
    //   visible: true なら表示する。false なら表示しない（窓は作るので、描く・保存することはできる）
    //   窓が作れなければ InvalidOperationException（中に元の例外）。
    //   例: var spectrum = new SpectrumPlot("graph_visualizer spectrum", "Wavelength / nm", "Absorbance", true);
    public SpectrumPlot(string title, string xLabel, string yLabel, bool visible)
    {
        _host = new UiThreadHost<SpectrumPlotForm>("SpectrumPlot UI", "spectrum plot", () =>
        {
            var form = new SpectrumPlotForm(title, xLabel, yLabel);
            form.RedrawRequested = RedrawIfChanged;
            return form;
        }, visible);
    }

    // SetWavelengths — スペクトルの横軸の点（波長の格子）を決める。吸光度はすべて 0 に戻す（次の描き直しで出る）
    //   wavelengths: 波長の並び（1 点以上・すべて有限。写しを持つ）
    //   空・有限でない値があれば ArgumentException、UI スレッドが例外で壊れていれば InvalidOperationException。
    //   例: spectrum.SetWavelengths(new double[] { 200.0, 202.0, 204.0 });
    public void SetWavelengths(double[] wavelengths)
    {
        if (wavelengths.Length == 0) throw new ArgumentException("wavelengths must have at least one point");
        foreach (double wavelength in wavelengths)
        {
            if (!double.IsFinite(wavelength)) throw new ArgumentException($"wavelengths must be finite, got {wavelength}");
        }
        lock (_stateLock)
        {
            _host.ThrowIfFailed();
            _wavelengths = (double[])wavelengths.Clone();
            _absorbances = new double[wavelengths.Length];
            _hasUnrenderedChange = true;
        }
    }

    // SetAbsorbances — スペクトルの縦軸の値（各波長の吸光度）を書き換える。UI スレッドを待たずに戻り、次の描き直し
    //   （RedrawInterval ごと）で窓に出る。窓が閉じた後も写しは書き換わる（描かないだけ）
    //   absorbances: 吸光度の並び（SetWavelengths の波長と同じ長さ。写しを持つ）
    //   長さが違えば ArgumentException、UI スレッドが例外で壊れていれば InvalidOperationException。
    //   例: spectrum.SetAbsorbances(new double[] { 0.0, 0.1, 0.0 });
    public void SetAbsorbances(double[] absorbances)
    {
        lock (_stateLock)
        {
            _host.ThrowIfFailed();
            if (absorbances.Length != _wavelengths.Length)
                throw new ArgumentException($"absorbances has {absorbances.Length} points but there are {_wavelengths.Length} wavelengths");
            Array.Copy(absorbances, _absorbances, absorbances.Length);
            _hasUnrenderedChange = true;
        }
    }

    // PointCount / WavelengthAt / AbsorbanceAt — 写しの点の数・番号 index の波長と吸光度（テスト用。範囲外は IndexOutOfRangeException）
    //   例: double peakAbsorbance = spectrum.AbsorbanceAt(125);
    public long PointCount()
    {
        lock (_stateLock)
        {
            return _wavelengths.Length;
        }
    }

    public double WavelengthAt(long index)
    {
        lock (_stateLock)
        {
            return _wavelengths[index];
        }
    }

    public double AbsorbanceAt(long index)
    {
        lock (_stateLock)
        {
            return _absorbances[index];
        }
    }

    // SetAxisLimits — 軸の範囲を決めて描き直す（ダブルクリック・中クリックでこの範囲に戻る）
    //   xMin, xMax: 横軸の左端・右端、yMin, yMax: 縦軸の下端・上端（有限で、それぞれ min < max）
    //   範囲がおかしければ ArgumentException、窓が閉じていれば InvalidOperationException。
    //   例: spectrum.SetAxisLimits(200.0, 800.0, 0.0, 1.0);
    public void SetAxisLimits(double xMin, double xMax, double yMin, double yMax)
    {
        CheckRange(xMin, xMax, "x");
        CheckRange(yMin, yMax, "y");
        _host.Run(() =>
        {
            _host.Form.SetAxisLimits(new ScottPlot.AxisLimits(xMin, xMax, yMin, yMax));
            return 0L;
        });
    }

    // RenderNow — まだ描いていない書き換えを今すぐ描く（UI スレッドで描き終わるまで待つ）
    //   窓が閉じていれば・UI スレッドが例外で壊れていれば InvalidOperationException。
    //   例: spectrum.RenderNow();
    public void RenderNow()
    {
        _host.Run(() =>
        {
            RedrawIfChanged();
            _host.Form.RenderPlot();
            return 0L;
        });
    }

    // SavePng — 今の図を width × height ピクセルの PNG に描いて path へ保存する（窓の大きさ・表示によらない。あれば上書き）
    //   大きさが 1 未満なら ArgumentOutOfRangeException、窓が閉じていれば InvalidOperationException。
    //   例: spectrum.SavePng("spectrum.png", 800, 450);
    public void SavePng(string path, long width, long height)
    {
        if (width < 1 || width > 16384) throw new ArgumentOutOfRangeException(nameof(width), width, "width must be 1..16384");
        if (height < 1 || height > 16384) throw new ArgumentOutOfRangeException(nameof(height), height, "height must be 1..16384");
        _host.Run(() =>
        {
            RedrawIfChanged();
            _host.Form.SavePng(path, (int)width, (int)height);
            return 0L;
        });
    }

    // IsOpen — 窓が開いているか（利用者が閉じるボタンを押した後・Close の後は false）
    //   例: if (!spectrum.IsOpen()) { ... }
    public bool IsOpen() => _host.IsOpen;

    // Close — 窓を閉じ、UI スレッドが終わるのを待つ（最大 5 秒）。閉じた後に呼んでも何もしない
    //   例: spectrum.Close();
    public void Close() => _host.Close();

    // RedrawIfChanged — （UI スレッド）まだ描いていない書き換えがあれば、写しを窓の線へ写して描き直す
    private void RedrawIfChanged()
    {
        lock (_stateLock)
        {
            if (!_hasUnrenderedChange) return;
            _host.Form.ShowSpectrum(_wavelengths, _absorbances);
            _hasUnrenderedChange = false;
        }
        _host.Form.RenderPlot();
    }

    // CheckRange — 軸の範囲が有限で minimum < maximum か確かめる。そうでなければ ArgumentException
    //   axisName: メッセージに出す軸の名前（"x" / "y"）
    private static void CheckRange(double minimum, double maximum, string axisName)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum >= maximum)
            throw new ArgumentException($"{axisName} axis limits must be finite with minimum < maximum, got {minimum} .. {maximum}");
    }
}

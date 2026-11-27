using System;
using System.Collections.Generic;

namespace TimeSeriesPlotBridge;

// TimeSeriesPlot — 輪のように使う固定長の時系列を、縦に並べたグラフ（パネル）に ScottPlot（FormsPlot）で描く WinForms の窓。
// Arrow から import[cs-dll] で使う（Arrow の型スタブに出るクラス。もう 1 つは SpectrumPlot）
//
// 窓は専用の STA スレッドで動く（UiThreadHost）。Arrow のメインスレッドは SetPoint などで値の写しを
// 書き換えるだけで、UI スレッドを待たない（Invoke しない）。UI スレッドは RedrawInterval ごとに、書き換えがあれば
// 写しを古い順に並べ替えて描き直す。Arrow から使うときは time_series_plot/__init__.ar の TimeSeriesWindow を通す。
//
// パネルと系列:
//   パネル（AddPanel で上から順に足す）は、左軸の系列（主の系列。色はパネルごと。最新の点を赤い点で強調）を 1 本持つ。
//   ShowOverlay したパネルには、窓に 1 本だけある重ね書きの系列（SetOverlayPoint で書く。電位など）を右軸に淡色で描く。
// 点の並び（Arrow 側の RingSeries と同じ）:
//   長さ capacity の配列を輪のように使う。最新の点の番号 newestIndex の次（newestIndex + 1、最後なら 0）が最も古い点。
//   最初はすべて (0.0, 0.0) で、newestIndex は capacity - 1（番号 0 から書き始める）。
//
// メンバー:
//   _host: UiThreadHost<TimeSeriesPlotForm> — 窓と、その UI スレッド（UI スレッドの例外の記録も持つ）
//   _form: TimeSeriesPlotForm   — 窓（_host.Form。UI スレッドだけで触る。ほかのスレッドからは _host.Run で頼む）
//   _stateLock: object          — _panels / _overlay / _hasUnrenderedChange を守る lock
//   _capacity: int              — 系列の点の数
//   _panels: List<PanelState>   — パネルごとの主の系列の写しと見た目（番号がパネルの番号）
//   _overlay: SeriesBuffer      — 重ね書きの系列の写し
//   _hasUnrenderedChange: bool  — まだ描いていない書き換えがあるか
//   TimeSeriesPlot(title, capacity, xLabel, visible) — 窓を作る（パネルは無い。visible が false なら表示しない・テスト用）
//   AddPanel(title, yLabel, red, green, blue) -> long — 一番下にパネルを足して番号を返す
//   ShowOverlay(panel, label)   — パネルに重ね書きの系列を右軸（名前 label）で描く
//   SetPanelTitle(panel, title, yLabel), SetPanelColor(panel, red, green, blue), SetDataVisible(panel, visible) — パネルの見た目
//   SetPoint(panel, index, x, y) — パネルの主の系列の番号 index の点を (x, y) にし、それを最新の点にする（待たずに戻る）
//   SetOverlayPoint(index, x, y) — 重ね書きの系列の点を書く（待たずに戻る）
//   XAt / YAt(panel, index), NewestIndex(panel), OverlayXAt / OverlayYAt(index), OverlayNewestIndex() — 写しの値（テスト用）
//   PanelCount() -> long, Capacity() -> long — パネルの数・点の数
//   RenderNow()                 — 書き換えを今すぐ描く（描き終わるまで待つ）
//   IsFollowingLatest(panel) -> bool — パネルの軸の範囲を最新の点に合わせ続けているか
//   SavePng(path, width, height) — 全パネルを縦に並べて PNG に保存する（描き終わるまで待つ）
//   IsOpen() -> bool, Close()   — 窓が開いているか・窓を閉じる
//
// 例（Arrow から・ふつうは TimeSeriesWindow を使う）:
//   import[cs-dll] .TimeSeriesPlotBridge as bridge
//   let plot = bridge.TimeSeriesPlot("graph_visualizer plots", 300, "Period", True)
//   let current_panel = plot.AddPanel("Faradaic current", "j", 255, 127, 14)
//   plot.ShowOverlay(current_panel, "E / V")
//   plot.SetPoint(current_panel, 0, 0.0167, -0.12)
//   plot.SetOverlayPoint(0, 0.0167, 0.004)
public sealed class TimeSeriesPlot
{
    private readonly UiThreadHost<TimeSeriesPlotForm> _host;
    private readonly object _stateLock = new();
    private readonly int _capacity;
    private readonly List<PanelState> _panels = new();
    private readonly SeriesBuffer _overlay;
    private bool _hasUnrenderedChange;

    // TimeSeriesPlot — 専用の STA スレッドでパネルの無い窓を作り、窓ができるまで待って戻る
    //   title:    窓のタイトル
    //   capacity: 系列の点の数（1 以上）。Arrow 側の RingSeries の容量と同じにする
    //   xLabel:   横軸の名前（一番下のパネルにだけ出す）
    //   visible:  true なら表示する。false なら表示しない（窓は作るので、点を書く・描く・保存することはできる）
    //   capacity が 1 未満なら ArgumentOutOfRangeException、窓が作れなければ InvalidOperationException（中に元の例外）。
    //   例: var plot = new TimeSeriesPlot("graph_visualizer plots", 300, "Period", true);
    public TimeSeriesPlot(string title, long capacity, string xLabel, bool visible)
    {
        if (capacity < 1 || capacity > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "capacity must be 1 or more");
        _capacity = (int)capacity;
        _overlay = new SeriesBuffer(_capacity);

        _host = new UiThreadHost<TimeSeriesPlotForm>("TimeSeriesPlot UI", "time series plot", () =>
        {
            var form = new TimeSeriesPlotForm(title, xLabel, _capacity);
            form.RedrawRequested = RedrawIfChanged;
            return form;
        }, visible);
    }

    // _form — 窓（UI スレッドだけで触る）
    private TimeSeriesPlotForm _form => _host.Form;

    // AddPanel — 一番下にパネルを足す（パネルは窓の高さを等分する）。主の系列の点はすべて (0.0, 0.0)
    //   title:  パネルの題名（図の上に出す）
    //   yLabel: 左軸の名前
    //   red, green, blue: 主の系列の線の色（0〜255）
    //   戻り値: パネルの番号（足した順に 0, 1, 2, …）
    //   色が範囲外なら ArgumentOutOfRangeException、窓が閉じていれば InvalidOperationException。
    //   例: long currentPanel = plot.AddPanel("Faradaic current", "j", 255, 127, 14);
    public long AddPanel(string title, string yLabel, long red, long green, long blue)
    {
        ScottPlot.Color lineColor = CheckedColor(red, green, blue);
        return RunOnUiThread(() =>
        {
            int panelIndex = _form.AddPanel(title, yLabel, lineColor);
            lock (_stateLock)
            {
                _panels.Add(new PanelState(_capacity, title, yLabel, lineColor));
                _hasUnrenderedChange = true;
            }
            return panelIndex;
        });
    }

    // ShowOverlay — パネルに重ね書きの系列（SetOverlayPoint で書くもの）を、右軸に淡色で描く
    //   panel: パネルの番号、label: 右軸の名前
    //   窓が閉じていれば InvalidOperationException。
    //   例: plot.ShowOverlay(currentPanel, "E / V");
    public void ShowOverlay(long panel, string label)
    {
        RunOnUiThread(() =>
        {
            int panelIndex;
            lock (_stateLock)
            {
                panelIndex = CheckedIndex(panel, _panels.Count, "panel");
                _hasUnrenderedChange = true;
            }
            _form.ShowOverlay(panelIndex, label);
            return 0L;
        });
    }

    // SetPanelTitle — パネルの題名と左軸の名前を変える（次の描き直しで出る。待たずに戻る。窓が閉じていても例外にしない）
    //   例: plot.SetPanelTitle(speciesPanel, "H_ads", "quantity");
    public void SetPanelTitle(long panel, string title, string yLabel)
    {
        lock (_stateLock)
        {
            ThrowIfUiThreadFailed();
            PanelState panelState = _panels[CheckedIndex(panel, _panels.Count, "panel")];
            panelState.Title = title;
            panelState.YLabel = yLabel;
            panelState.HasStyleChange = true;
            _hasUnrenderedChange = true;
        }
    }

    // SetPanelColor — パネルの主の系列の線の色を変える（次の描き直しで出る。待たずに戻る）
    //   red, green, blue: 0〜255（範囲外なら ArgumentOutOfRangeException）
    //   例: plot.SetPanelColor(speciesPanel, 230, 41, 55);
    public void SetPanelColor(long panel, long red, long green, long blue)
    {
        ScottPlot.Color lineColor = CheckedColor(red, green, blue);
        lock (_stateLock)
        {
            ThrowIfUiThreadFailed();
            PanelState panelState = _panels[CheckedIndex(panel, _panels.Count, "panel")];
            panelState.LineColor = lineColor;
            panelState.HasStyleChange = true;
            _hasUnrenderedChange = true;
        }
    }

    // SetDataVisible — パネルの主の系列（線と赤い点）を描くか（重ね書きの系列は描き続ける。待たずに戻る）
    //   例: plot.SetDataVisible(speciesPanel, false);   // 化学種が選ばれていないとき
    public void SetDataVisible(long panel, bool visible)
    {
        lock (_stateLock)
        {
            ThrowIfUiThreadFailed();
            PanelState panelState = _panels[CheckedIndex(panel, _panels.Count, "panel")];
            panelState.DataVisible = visible;
            panelState.HasStyleChange = true;
            _hasUnrenderedChange = true;
        }
    }

    // SetPoint — パネルの主の系列の番号 index の点を (x, y) にし、それを最新の点（赤い点）にする。UI スレッドを待たずに戻り、
    //   次の描き直し（RedrawInterval ごと）で窓に出る。窓が閉じた後も写しは書き換わる（描かないだけ）
    //   panel: パネルの番号、index: 点の番号（0 以上 capacity 未満）、x, y: 点の横軸・縦軸の値
    //   番号が範囲外なら ArgumentOutOfRangeException、UI スレッドが例外で壊れていれば InvalidOperationException。
    //   例: plot.SetPoint(currentPanel, 0, 0.0167, -0.12);
    public void SetPoint(long panel, long index, double x, double y)
    {
        lock (_stateLock)
        {
            ThrowIfUiThreadFailed();
            _panels[CheckedIndex(panel, _panels.Count, "panel")].Write(CheckedIndex(index, _capacity, "point"), x, y);
            _hasUnrenderedChange = true;
        }
    }

    // SetOverlayPoint — 重ね書きの系列の番号 index の点を (x, y) にし、それを最新の点にする（待たずに戻る）
    //   例: plot.SetOverlayPoint(0, 0.0167, 0.004);
    public void SetOverlayPoint(long index, double x, double y)
    {
        lock (_stateLock)
        {
            ThrowIfUiThreadFailed();
            _overlay.Write(CheckedIndex(index, _capacity, "point"), x, y);
            _hasUnrenderedChange = true;
        }
    }

    // XAt / YAt / NewestIndex — パネルの主の系列の写しの値・最新の点の番号（テスト用）
    //   例: double latestY = plot.YAt(0, plot.NewestIndex(0));
    public double XAt(long panel, long index)
    {
        lock (_stateLock)
        {
            return _panels[CheckedIndex(panel, _panels.Count, "panel")].XValues[CheckedIndex(index, _capacity, "point")];
        }
    }

    public double YAt(long panel, long index)
    {
        lock (_stateLock)
        {
            return _panels[CheckedIndex(panel, _panels.Count, "panel")].YValues[CheckedIndex(index, _capacity, "point")];
        }
    }

    public long NewestIndex(long panel)
    {
        lock (_stateLock)
        {
            return _panels[CheckedIndex(panel, _panels.Count, "panel")].NewestIndex;
        }
    }

    // OverlayXAt / OverlayYAt / OverlayNewestIndex — 重ね書きの系列の写しの値・最新の点の番号（テスト用）
    //   例: double latestPotential = plot.OverlayYAt(plot.OverlayNewestIndex());
    public double OverlayXAt(long index)
    {
        lock (_stateLock)
        {
            return _overlay.XValues[CheckedIndex(index, _capacity, "point")];
        }
    }

    public double OverlayYAt(long index)
    {
        lock (_stateLock)
        {
            return _overlay.YValues[CheckedIndex(index, _capacity, "point")];
        }
    }

    public long OverlayNewestIndex()
    {
        lock (_stateLock)
        {
            return _overlay.NewestIndex;
        }
    }

    // PanelCount — パネルの数
    //   例: long panelCount = plot.PanelCount();
    public long PanelCount()
    {
        lock (_stateLock)
        {
            return _panels.Count;
        }
    }

    // Capacity — 系列の点の数
    //   例: long count = plot.Capacity();
    public long Capacity() => _capacity;

    // RenderNow — まだ描いていない書き換えを今すぐ描く（UI スレッドで描き終わるまで待つ）
    //   窓が閉じていれば・UI スレッドが例外で壊れていれば InvalidOperationException。
    //   例: plot.RenderNow();
    public void RenderNow()
    {
        RunOnUiThread(() =>
        {
            RedrawIfChanged();
            return 0L;
        });
    }

    // IsFollowingLatest — パネルの軸の範囲を最新の点に合わせ続けているか。利用者がそのパネルの図を動かす・拡大縮小すると
    //   false になり、ダブルクリック・中クリックで true に戻る（TimeSeriesPlotForm を参照）
    //   窓が閉じていれば・UI スレッドが例外で壊れていれば InvalidOperationException。
    //   例: bool following = plot.IsFollowingLatest(0);
    public bool IsFollowingLatest(long panel)
    {
        return RunOnUiThread(() =>
        {
            int panelIndex;
            lock (_stateLock)
            {
                panelIndex = CheckedIndex(panel, _panels.Count, "panel");
            }
            return _form.IsFollowingLatest(panelIndex) ? 1L : 0L;
        }) != 0L;
    }

    // SavePng — 今の図（全パネルを上から順に縦に並べる）を width × height ピクセルの PNG に描いて path へ保存する
    //   （窓の大きさ・表示によらない。あれば上書き）
    //   大きさが 1 未満なら ArgumentOutOfRangeException、窓が閉じていれば InvalidOperationException。
    //   例: plot.SavePng("plots.png", 800, 900);
    public void SavePng(string path, long width, long height)
    {
        if (width < 1 || width > 16384) throw new ArgumentOutOfRangeException(nameof(width), width, "width must be 1..16384");
        if (height < 1 || height > 16384) throw new ArgumentOutOfRangeException(nameof(height), height, "height must be 1..16384");
        RunOnUiThread(() =>
        {
            RedrawIfChanged();
            _form.SavePng(path, (int)width, (int)height);
            return 0L;
        });
    }

    // IsOpen — 窓が開いているか（利用者が閉じるボタンを押した後・Close の後は false）
    //   例: if (!plot.IsOpen()) { ... }
    public bool IsOpen() => _host.IsOpen;

    // Close — 窓を閉じ、UI スレッドが終わるのを待つ（最大 5 秒）。閉じた後に呼んでも何もしない
    //   例: plot.Close();
    public void Close() => _host.Close();

    // RedrawIfChanged — （UI スレッド）まだ描いていない書き換えがあれば、見た目の変更を当て、写しを古い順に並べて全パネルを描き直す
    private void RedrawIfChanged()
    {
        lock (_stateLock)
        {
            if (!_hasUnrenderedChange) return;
            for (int panelIndex = 0; panelIndex < _panels.Count; panelIndex++)
            {
                PanelState panelState = _panels[panelIndex];
                if (panelState.HasStyleChange)
                {
                    _form.ApplyPanelStyle(panelIndex, panelState.Title, panelState.YLabel, panelState.LineColor, panelState.DataVisible);
                    panelState.HasStyleChange = false;
                }
                _form.CopyPointsOldestFirst(panelIndex, panelState.XValues, panelState.YValues, panelState.NewestIndex);
            }
            _form.CopyOverlayOldestFirst(_overlay.XValues, _overlay.YValues, _overlay.NewestIndex);
            _hasUnrenderedChange = false;
        }
        _form.RenderPanels();
    }

    // RunOnUiThread — action を UI スレッドで実行して結果を返す（例外は呼んだスレッドへ投げ直される）。
    //   窓が閉じていれば・UI スレッドが例外で壊れていれば InvalidOperationException
    private long RunOnUiThread(Func<long> action) => _host.Run(action);

    // ThrowIfUiThreadFailed — UI スレッドで例外が起きていれば、それを中に入れた InvalidOperationException を投げる
    private void ThrowIfUiThreadFailed() => _host.ThrowIfFailed();

    // CheckedIndex — Arrow の int（C# の long）の番号を確かめて int にする。範囲外なら ArgumentOutOfRangeException
    //   what: メッセージに出す番号の種類（"panel" / "point"）
    private static int CheckedIndex(long index, int count, string what)
    {
        if (index < 0 || index >= count)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"{what} index must be 0..{count - 1}");
        return (int)index;
    }

    // CheckedColor — 0〜255 の r, g, b を確かめて不透明な ScottPlot の色にする。範囲外なら ArgumentOutOfRangeException
    private static ScottPlot.Color CheckedColor(long red, long green, long blue)
    {
        foreach (long component in new[] { red, green, blue })
        {
            if (component < 0 || component > 255)
                throw new ArgumentOutOfRangeException(nameof(red), component, "color components must be 0..255");
        }
        return new ScottPlot.Color((byte)red, (byte)green, (byte)blue, (byte)255);
    }
}

// SeriesBuffer — 輪のように使う固定長の系列の写し（TimeSeriesPlot の _stateLock の中だけで触る）
//
// メンバー:
//   XValues, YValues: double[] — 点の値（番号順。最初はすべて 0.0）
//   NewestIndex: int           — 最新の点の番号（最初は capacity - 1）
//   Write(index, x, y)         — 番号 index の点を書き、それを最新の点にする
// 例: var overlay = new SeriesBuffer(300); overlay.Write(0, 0.0167, 0.004);
internal class SeriesBuffer
{
    public readonly double[] XValues;
    public readonly double[] YValues;
    public int NewestIndex;

    public SeriesBuffer(int capacity)
    {
        XValues = new double[capacity];
        YValues = new double[capacity];
        NewestIndex = capacity - 1;
    }

    public void Write(int index, double x, double y)
    {
        XValues[index] = x;
        YValues[index] = y;
        NewestIndex = index;
    }
}

// PanelState — パネルの主の系列の写しと、まだ窓に当てていない見た目（TimeSeriesPlot の _stateLock の中だけで触る）
//
// メンバー（SeriesBuffer に加えて）:
//   Title, YLabel: string       — パネルの題名・左軸の名前
//   LineColor: ScottPlot.Color  — 主の系列の線の色
//   DataVisible: bool           — 主の系列を描くか
//   HasStyleChange: bool        — 見た目をまだ窓に当てていないか（RedrawIfChanged が当てて false にする）
// 例: var panelState = new PanelState(300, "Faradaic current", "j", ScottPlot.Color.FromHex("#ff7f0e"));
internal sealed class PanelState : SeriesBuffer
{
    public string Title;
    public string YLabel;
    public ScottPlot.Color LineColor;
    public bool DataVisible = true;
    public bool HasStyleChange;

    public PanelState(int capacity, string title, string yLabel, ScottPlot.Color lineColor) : base(capacity)
    {
        Title = title;
        YLabel = yLabel;
        LineColor = lineColor;
    }
}

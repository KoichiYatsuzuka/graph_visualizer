using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using ScottPlot.Interactivity;
using ScottPlot.Interactivity.UserActionResponses;
using ScottPlot.WinForms;
using SkiaSharp;

namespace TimeSeriesPlotBridge;

// TimeSeriesPlotForm — 時系列のグラフ（PlotPanel）を縦に並べるフォーム。作ったスレッド（UI スレッド）からだけ触る
//
// パネルは表（1 列の TableLayoutPanel）の行で、窓の高さを等分する。横軸の名前は一番下のパネルにだけ出す。
// 各パネルの操作・追従は PlotPanel を参照。
//
// メンバー:
//   RedrawInterval: int            — 描き直しを確かめる間隔 [ms]
//   RedrawRequested: Action?       — RedrawInterval ごとに UI スレッドで呼ぶ（TimeSeriesPlot が書き換えを描く）
//   _xLabel: string                — 横軸の名前
//   _capacity: int                 — 系列の点の数
//   _panelTable: TableLayoutPanel  — パネルを縦に並べる表
//   _plotPanels: List<PlotPanel>   — 上から順のパネル（添字がパネルの番号）
//   _redrawTimer: Timer            — RedrawRequested を呼ぶタイマー
//   _objectQueryBlockers: List<ObjectQueryBlocker> — 表とその中の窓の WM_GETOBJECT を止めるもの（GC されないよう持つ）
//   AddPanel(title, yLabel, lineColor) -> int — 一番下にパネルを足して番号を返す
//   ShowOverlay(panelIndex, label) — パネルに重ね書きの系列を右軸で描く
//   ApplyPanelStyle(panelIndex, title, yLabel, lineColor, dataVisible) — パネルの見た目を変える
//   CopyPointsOldestFirst(panelIndex, xValues, yValues, newestIndex) — パネルの主の系列を写す
//   CopyOverlayOldestFirst(xValues, yValues, newestIndex) — 重ね書きの系列を、それを描く全パネルへ写す
//   RenderPanels()                 — 全パネルを描き直す
//   IsFollowingLatest(panelIndex) -> bool — パネルが最新の点に追従しているか
//   SavePng(path, width, height)   — 全パネルを縦に並べて PNG で保存する
//
// 例（UI スレッドで）:
//   var form = new TimeSeriesPlotForm("graph_visualizer plots", "Period", 300);
//   int currentPanel = form.AddPanel("Faradaic current", "j", ScottPlot.Color.FromHex("#ff7f0e"));
//   form.ShowOverlay(currentPanel, "E / V");
//   form.Show();
//
// ⚠ NativeAOT の WinForms では UI Automation が動かないので、フォームは WndProc で、表と中の窓（FormsPlot・TransparentSKControl）は
//   ObjectQueryBlocker で WM_GETOBJECT を止める（下の NativeAccessibility を参照）。
internal sealed class TimeSeriesPlotForm : Form
{
    private const int RedrawInterval = 33;
    private const int InitialClientWidth = 640;
    private const int InitialClientHeight = 780;

    private readonly string _xLabel;
    private readonly int _capacity;
    private readonly TableLayoutPanel _panelTable;
    private readonly List<PlotPanel> _plotPanels = new();
    private readonly System.Windows.Forms.Timer _redrawTimer;
    private readonly List<ObjectQueryBlocker> _objectQueryBlockers = new();

    public Action? RedrawRequested;

    // TimeSeriesPlotForm — パネルの無いフォームを作る。表示はしない（Show は呼んだ側が決める）
    //   title:  窓のタイトル
    //   xLabel: 横軸の名前（一番下のパネルにだけ出す）
    //   capacity: 系列の点の数（1 以上）
    //   例: var form = new TimeSeriesPlotForm("graph_visualizer plots", "Period", 300);
    public TimeSeriesPlotForm(string title, string xLabel, int capacity)
    {
        Text = title;
        _xLabel = xLabel;
        _capacity = capacity;
        ClientSize = new Size(InitialClientWidth, InitialClientHeight);
        SkiaNativeLoader.EnsureLoaded();

        _panelTable = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 0 };
        _panelTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0f));
        Controls.Add(_panelTable);
        ObjectQueryBlocker.AttachToTree(_panelTable, _objectQueryBlockers);

        _redrawTimer = new System.Windows.Forms.Timer { Interval = RedrawInterval };
        _redrawTimer.Tick += (_, _) => RedrawRequested?.Invoke();
        _redrawTimer.Start();
        FormClosed += (_, _) => _redrawTimer.Stop();
    }

    // WndProc — WM_GETOBJECT だけ WinForms の UI Automation を通さずに OS へ渡す（NativeAccessibility を参照）
    protected override void WndProc(ref Message message)
    {
        if (NativeAccessibility.IsObjectQuery(message)) { DefWndProc(ref message); return; }
        base.WndProc(ref message);
    }

    // AddPanel — 一番下にパネルを足し、全パネルの高さを等分し直す。横軸の名前は新しいパネルへ移す
    //   title: パネルの題名、yLabel: 左軸の名前、lineColor: 主の系列の線の色
    //   戻り値: パネルの番号（足した順に 0, 1, 2, …）
    //   例: int currentPanel = form.AddPanel("Faradaic current", "j", ScottPlot.Color.FromHex("#ff7f0e"));
    public int AddPanel(string title, string yLabel, ScottPlot.Color lineColor)
    {
        var plotPanel = new PlotPanel(title, yLabel, lineColor, _capacity);
        if (_plotPanels.Count > 0) _plotPanels[_plotPanels.Count - 1].SetXLabel("");
        plotPanel.SetXLabel(_xLabel);
        _plotPanels.Add(plotPanel);

        _panelTable.SuspendLayout();
        _panelTable.RowCount = _plotPanels.Count;
        _panelTable.RowStyles.Clear();
        for (int rowIndex = 0; rowIndex < _plotPanels.Count; rowIndex++)
            _panelTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0f / _plotPanels.Count));
        _panelTable.Controls.Add(plotPanel.Control, 0, _plotPanels.Count - 1);
        _panelTable.ResumeLayout();
        return _plotPanels.Count - 1;
    }

    // ShowOverlay — パネルに重ね書きの系列を右軸（名前 label）に淡色で描く
    //   例: form.ShowOverlay(1, "E / V");
    public void ShowOverlay(int panelIndex, string label) => _plotPanels[panelIndex].ShowOverlay(label);

    // ApplyPanelStyle — パネルの題名・左軸の名前・主の系列の色・主の系列を描くかを変える（次の RenderPanels から出る）
    //   例: form.ApplyPanelStyle(2, "H_ads", "quantity", new ScottPlot.Color(230, 41, 55, 255), true);
    public void ApplyPanelStyle(int panelIndex, string title, string yLabel, ScottPlot.Color lineColor, bool dataVisible)
        => _plotPanels[panelIndex].ApplyStyle(title, yLabel, lineColor, dataVisible);

    // CopyPointsOldestFirst — パネルの主の系列を、最も古い点から最新の点の順に並べて写す（PlotPanel.CopyPointsOldestFirst）
    //   例: form.CopyPointsOldestFirst(0, xValues, yValues, 4);
    public void CopyPointsOldestFirst(int panelIndex, double[] xValues, double[] yValues, int newestIndex)
        => _plotPanels[panelIndex].CopyPointsOldestFirst(xValues, yValues, newestIndex);

    // CopyOverlayOldestFirst — 重ね書きの系列を、それを描く全パネルへ古い順に並べて写す
    //   例: form.CopyOverlayOldestFirst(xValues, yValues, 4);
    public void CopyOverlayOldestFirst(double[] xValues, double[] yValues, int newestIndex)
    {
        foreach (PlotPanel plotPanel in _plotPanels) plotPanel.CopyOverlayOldestFirst(xValues, yValues, newestIndex);
    }

    // RenderPanels — 全パネルを描き直す（追従しているパネルは軸の範囲を点に合わせる）
    //   例: form.RenderPanels();
    public void RenderPanels()
    {
        foreach (PlotPanel plotPanel in _plotPanels) plotPanel.Render();
    }

    // IsFollowingLatest — パネルが軸の範囲を最新の点に合わせ続けているか
    //   例: bool following = form.IsFollowingLatest(0);
    public bool IsFollowingLatest(int panelIndex) => _plotPanels[panelIndex].IsFollowingLatest;

    // SavePng — 全パネルを上から順に、width × height を等分した高さで縦に並べて PNG で path へ保存する（今の軸の範囲で描く）
    //   パネルが無ければ白い画像になる。パネルごとに別の画像に描いてから貼る（ScottPlot の Render は描き先の canvas 全体を
    //   背景で塗るので、1 枚の canvas の中の矩形に順に描くと前のパネルが消える。2026-11-26 実測）。
    //   例: form.SavePng("plots.png", 800, 900);
    public void SavePng(string path, int width, int height)
    {
        var imageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKSurface surface = SKSurface.Create(imageInfo);
        surface.Canvas.Clear(SKColors.White);
        for (int panelIndex = 0; panelIndex < _plotPanels.Count; panelIndex++)
        {
            int panelTop = height * panelIndex / _plotPanels.Count;
            int panelBottom = height * (panelIndex + 1) / _plotPanels.Count;
            using SKImage panelImage = _plotPanels[panelIndex].RenderImage(width, Math.Max(1, panelBottom - panelTop));
            surface.Canvas.DrawImage(panelImage, 0.0f, panelTop);
        }
        using SKImage image = surface.Snapshot();
        using SKData pngData = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, pngData.ToArray());
    }
}

// PlotPanel — 時系列のグラフ 1 枚（ScottPlot の FormsPlot）。主の系列（左軸）・最新の点（赤）・重ね書きの系列（右軸・淡色）を描く
//
// FormsPlot の操作（左ドラッグで移動・右ドラッグで拡大縮小・ホイールで拡大縮小・中ドラッグで範囲を選んで拡大・
// 矢印キーで移動）はそのまま使える。
//   追従: ふだんは描き直すたびに軸の範囲を全点に合わせる（最新の点を追う）。利用者が上の操作で範囲を変えると追従を止め、
//         その範囲のまま描き続ける（パネルの題名に paused と出す）。ダブルクリックか中クリックで追従に戻る。
//   範囲が変わったことは、前回合わせた左軸・下軸の範囲と今の範囲を比べて見つける（操作の種類によらない。LimitsChangedByUser）。
// 標準の操作の入れ替えは PlotInput を参照（右クリックのメニュー・ダブルクリックのベンチマーク表示・
// 中クリック / キーボードの自動調整は外す。メニューの保存のダイアログ・クリップボードは COM を使い、NativeAOT では動かない見込みのため
// （未確認）。中クリックとダブルクリックは「追従に戻る」に置き換える。
//
// メンバー:
//   Control: FormsPlot             — 表に置くコントロール（図は Control.Plot）
//   _title: string                 — パネルの題名（追従を止めているときは後ろに PausedTitleSuffix を足して出す）
//   _orderedX, _orderedY: double[] — 古い順に並べた主の系列（_trace が参照する配列。中身だけ書き換える）
//   _overlayX, _overlayY: double[] — 古い順に並べた重ね書きの系列（_overlayTrace が参照する配列）
//   _trace: Scatter                — 主の系列の線（左軸）
//   _newestMarker: Marker          — 主の系列の最新の点（赤・白い縁）
//   _overlayTrace: Scatter?        — 重ね書きの系列の線（右軸・淡色。ShowOverlay の前は null）
//   _followsLatest: bool           — 描き直すたびに軸の範囲を全点に合わせるか
//   _limitsAfterAutoScale: AxisLimits? — 最後に合わせた左軸・下軸の範囲（利用者が変えたかを見つけるため）
//   IsFollowingLatest: bool        — 追従しているか
//   SetXLabel(label), ShowOverlay(label), ApplyStyle(title, yLabel, lineColor, dataVisible) — 見た目
//   CopyPointsOldestFirst(...), CopyOverlayOldestFirst(...) — 輪の配列を古い順に並べて写す
//   Render()                       — （追従していれば軸の範囲を点に合わせて）描き直す
//   FollowLatest()                 — 追従に戻って描き直す
//   RenderImage(width, height) -> SKImage — 今の軸の範囲で width × height の画像に描く（PNG の保存用）
//
// 例（UI スレッドで）:
//   var plotPanel = new PlotPanel("Faradaic current", "j", ScottPlot.Color.FromHex("#ff7f0e"), 300);
//   plotPanel.ShowOverlay("E / V");
//   plotPanel.Render();
internal sealed class PlotPanel
{
    private const float TraceLineWidth = 2.0f;
    private const float OverlayLineWidth = 1.5f;
    private const double OverlayOpacity = 0.3;
    private const float NewestMarkerSize = 10.0f;
    private const float NewestMarkerOutlineWidth = 1.5f;
    private const float TitleFontSize = 13.0f;
    private const string PausedTitleSuffix = " — paused (double-click to follow)";
    private static readonly ScottPlot.Color NewestMarkerColor = ScottPlot.Color.FromHex("#e6202a");
    private static readonly ScottPlot.Color NewestMarkerOutlineColor = ScottPlot.Colors.White;
    private static readonly ScottPlot.Color OverlayColor = ScottPlot.Color.FromHex("#1f77b4").WithOpacity(OverlayOpacity);

    public readonly FormsPlot Control;
    private string _title;
    private readonly double[] _orderedX;
    private readonly double[] _orderedY;
    private readonly double[] _overlayX;
    private readonly double[] _overlayY;
    private readonly ScottPlot.Plottables.Scatter _trace;
    private readonly ScottPlot.Plottables.Marker _newestMarker;
    private ScottPlot.Plottables.Scatter? _overlayTrace;
    private bool _followsLatest = true;
    private ScottPlot.AxisLimits? _limitsAfterAutoScale;

    // PlotPanel — 点がすべて (0, 0) のグラフを作る
    //   title: 題名、yLabel: 左軸の名前、lineColor: 主の系列の線の色、capacity: 系列の点の数
    public PlotPanel(string title, string yLabel, ScottPlot.Color lineColor, int capacity)
    {
        _title = title;
        Control = new FormsPlot { Dock = DockStyle.Fill, Margin = new Padding(0) };
        ScottPlot.Plot plot = Control.Plot;
        _orderedX = new double[capacity];
        _orderedY = new double[capacity];
        _overlayX = new double[capacity];
        _overlayY = new double[capacity];
        _trace = plot.Add.ScatterLine(_orderedX, _orderedY, lineColor);
        _trace.LineWidth = TraceLineWidth;
        _newestMarker = plot.Add.Marker(0.0, 0.0, ScottPlot.MarkerShape.FilledCircle, NewestMarkerSize, NewestMarkerColor);
        _newestMarker.MarkerLineColor = NewestMarkerOutlineColor;
        _newestMarker.MarkerLineWidth = NewestMarkerOutlineWidth;
        plot.Axes.Title.Label.FontSize = TitleFontSize;
        plot.Title(title);
        plot.YLabel(yLabel);
        PlotInput.ReplaceDefaultResponses(Control, FollowLatest);
    }

    public bool IsFollowingLatest => _followsLatest;

    // SetXLabel — 下軸の名前（空文字なら出さない）
    public void SetXLabel(string label) => Control.Plot.XLabel(label);

    // ShowOverlay — 重ね書きの系列を右軸（名前 label）に淡色で描く（2 回目以降は右軸の名前を変えるだけ）
    public void ShowOverlay(string label)
    {
        if (_overlayTrace == null)
        {
            _overlayTrace = Control.Plot.Add.ScatterLine(_overlayX, _overlayY, OverlayColor);
            _overlayTrace.LineWidth = OverlayLineWidth;
            _overlayTrace.Axes.YAxis = Control.Plot.Axes.Right;
            Control.Plot.MoveToBack(_overlayTrace);
        }
        Control.Plot.Axes.Right.Label.Text = label;
    }

    // ApplyStyle — 題名・左軸の名前・主の系列の線の色・主の系列（線と赤い点）を描くかを変える
    public void ApplyStyle(string title, string yLabel, ScottPlot.Color lineColor, bool dataVisible)
    {
        _title = title;
        ShowTitle();
        Control.Plot.YLabel(yLabel);
        _trace.Color = lineColor;
        if (_trace.IsVisible != dataVisible) _limitsAfterAutoScale = null;   // 左軸に合わせる点が変わるので、比べる範囲を捨てる
        _trace.IsVisible = dataVisible;
        _newestMarker.IsVisible = dataVisible;
    }

    // CopyPointsOldestFirst — 輪のように使う配列の点を、最も古い点（newestIndex の次）から最新の点の順に並べて写し、
    //   赤い点を最新の点へ動かす
    //   xValues, yValues: 輪の配列（長さは capacity。書き換えない）、newestIndex: 最新の点の番号
    public void CopyPointsOldestFirst(double[] xValues, double[] yValues, int newestIndex)
    {
        CopyRingOldestFirst(xValues, yValues, newestIndex, _orderedX, _orderedY);
        _newestMarker.Location = new ScottPlot.Coordinates(xValues[newestIndex], yValues[newestIndex]);
    }

    // CopyOverlayOldestFirst — 重ね書きの系列を古い順に並べて写す（ShowOverlay の前は何もしない）
    public void CopyOverlayOldestFirst(double[] xValues, double[] yValues, int newestIndex)
    {
        if (_overlayTrace != null) CopyRingOldestFirst(xValues, yValues, newestIndex, _overlayX, _overlayY);
    }

    // Render — 描き直す。追従していれば、先に軸の範囲を今の点に合わせる。
    //   前回合わせた後に利用者が範囲を変えていれば（FormsPlot の操作）、追従を止めてその範囲のまま描く
    public void Render()
    {
        ScottPlot.Plot plot = Control.Plot;
        if (_followsLatest && _limitsAfterAutoScale is ScottPlot.AxisLimits limitsWeSet && LimitsChangedByUser(limitsWeSet, plot.Axes.GetLimits()))
            SetFollowing(false);
        if (_followsLatest)
        {
            plot.Axes.AutoScale();
            _limitsAfterAutoScale = plot.Axes.GetLimits();
        }
        Control.Refresh();
    }

    // FollowLatest — 追従に戻り、軸の範囲を今の点に合わせて描き直す（ダブルクリック・中クリックで呼ばれる）
    public void FollowLatest()
    {
        SetFollowing(true);
        _limitsAfterAutoScale = null;
        Render();
    }

    // RenderImage — 今の軸の範囲で width × height ピクセルの画像に描いて返す（呼んだ側が Dispose する）
    public SKImage RenderImage(int width, int height)
    {
        var imageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKSurface surface = SKSurface.Create(imageInfo);
        Control.Plot.Render(surface.Canvas, width, height);
        return surface.Snapshot();
    }

    // CopyRingOldestFirst — 輪の配列を、最も古い点（newestIndex の次）から最新の点の順に並べて orderedX, orderedY へ写す
    //   （ScottPlot の Scatter は作ったときの配列を参照するので、中身だけ書き換えれば次の描画に出る）。orderedX, orderedY を書き換える
    private static void CopyRingOldestFirst(double[] xValues, double[] yValues, int newestIndex, double[] orderedX, double[] orderedY)
    {
        int capacity = orderedX.Length;
        int oldestIndex = (newestIndex + 1) % capacity;
        for (int orderedIndex = 0; orderedIndex < capacity; orderedIndex++)
        {
            int ringIndex = (oldestIndex + orderedIndex) % capacity;
            orderedX[orderedIndex] = xValues[ringIndex];
            orderedY[orderedIndex] = yValues[ringIndex];
        }
    }

    // LimitsChangedByUser — 前回合わせた範囲 limitsWeSet と今の範囲 currentLimits が違うか（利用者が図を動かしたか）
    //   主の系列を描いていないときは左軸に点が無く、ScottPlot が描くときに左軸の範囲を既定の値に決めるので、下軸だけを比べる。
    //   範囲が決まっていない軸（NaN）どうしは同じとみなす（NaN は == で等しくならない）。
    private bool LimitsChangedByUser(ScottPlot.AxisLimits limitsWeSet, ScottPlot.AxisLimits currentLimits)
    {
        if (!SameValue(limitsWeSet.Left, currentLimits.Left) || !SameValue(limitsWeSet.Right, currentLimits.Right)) return true;
        if (!_trace.IsVisible) return false;
        return !SameValue(limitsWeSet.Bottom, currentLimits.Bottom) || !SameValue(limitsWeSet.Top, currentLimits.Top);
    }

    // SameValue — 2 つの値が等しいか（どちらも NaN なら等しい）
    private static bool SameValue(double first, double second) => first == second || (double.IsNaN(first) && double.IsNaN(second));

    // SetFollowing — 追従するかを変え、題名に出す
    private void SetFollowing(bool followsLatest)
    {
        if (_followsLatest == followsLatest) return;
        _followsLatest = followsLatest;
        ShowTitle();
    }

    // ShowTitle — 題名を出す（追従を止めていれば後ろに PausedTitleSuffix）
    private void ShowTitle() => Control.Plot.Title(_followsLatest ? _title : _title + PausedTitleSuffix);
}

// PlotInput — FormsPlot の標準の操作の入れ替え（PlotPanel と SpectrumPlotForm が使う）
//
// 外すもの: 右クリックのメニュー（画像の保存のダイアログ・クリップボードは COM を使い、NativeAOT では動かない見込み。未確認）、
//           ダブルクリックのベンチマーク表示、中クリック / キーボードの自動調整（呼んだ側の「元に戻す」と食い違う）。
// 足すもの: ダブルクリック（左）と中クリックで resetView を呼ぶ（TimeSeries は追従に戻る、スペクトルは決めた範囲に戻る）。
//
// メンバー:
//   ReplaceDefaultResponses(formsPlot, resetView) — 上の入れ替えをする
// 例: PlotInput.ReplaceDefaultResponses(Control, FollowLatest);
internal static class PlotInput
{
    public static void ReplaceDefaultResponses(FormsPlot formsPlot, Action resetView)
    {
        UserInputProcessor inputProcessor = formsPlot.UserInputProcessor;
        inputProcessor.RemoveAll<SingleClickContextMenu>();
        inputProcessor.RemoveAll<DoubleClickBenchmark>();
        inputProcessor.RemoveAll<SingleClickAutoscale>();
        inputProcessor.RemoveAll<KeyboardAutoscale>();
        inputProcessor.UserActionResponses.Add(new DoubleClickResponse(StandardMouseButtons.Left, (_, _) => resetView()));
        inputProcessor.UserActionResponses.Add(new SingleClickResponse(StandardMouseButtons.Middle, (_, _) => resetView()));
    }
}

// NativeAccessibility — WinForms の UI Automation を NativeAOT で使わないための判定（slider_panel/SliderPanelForm.cs と同じ）
//
// 支援技術や IME などが窓に WM_GETOBJECT を送ると、WinForms は UI Automation の provider を COM で返そうとし、
// NativeAOT には COM の marshalling が無いので NotSupportedException で落ちる。そこで WM_GETOBJECT だけは
// WinForms の処理を通さない。
//
// メンバー:
//   WM_GETOBJECT: int — Win32 のメッセージ番号
//   IsObjectQuery(message) -> bool — message が WM_GETOBJECT か
// 例: if (NativeAccessibility.IsObjectQuery(message)) { DefWndProc(ref message); return; }
internal static class NativeAccessibility
{
    private const int WM_GETOBJECT = 0x003D;

    internal static bool IsObjectQuery(Message message) => message.Msg == WM_GETOBJECT;
}

// ObjectQueryBlocker — 派生クラスを作れないコントロール（FormsPlot の中の TransparentSKControl など）の窓の前に割り込み、
// WM_GETOBJECT を 0（独自の支援技術向けのオブジェクトは無い。OS 標準の代理が使われる）で返して WinForms へ渡さない
//
// NativeWindow.AssignHandle は窓のプロシージャを差し替えるので、この WndProc がコントロール自身の WndProc より先に呼ばれる。
// コントロールのハンドルが作り直されたら（HandleCreated）付け直し、後から足された子（ControlAdded）にも付ける。
//
// メンバー:
//   _control: Control — 割り込む先のコントロール
//   AttachToTree(control, blockers) — control とその子孫すべてに付け、blockers に足す（GC されないよう持っておくリスト）
// 例: ObjectQueryBlocker.AttachToTree(_panelTable, _objectQueryBlockers);
internal sealed class ObjectQueryBlocker : NativeWindow
{
    private readonly Control _control;

    private ObjectQueryBlocker(Control control)
    {
        _control = control;
        _control.HandleCreated += (_, _) => AssignHandle(_control.Handle);
        _control.HandleDestroyed += (_, _) => ReleaseHandle();
        if (_control.IsHandleCreated) AssignHandle(_control.Handle);
    }

    // AttachToTree — control とその子孫すべてに ObjectQueryBlocker を付ける（後から足された子にも付く）
    //   blockers: 付けたものを足すリスト（呼んだ側が持ち続ける。後から足された子の分も同じリストに足す）。書き換える
    public static void AttachToTree(Control control, List<ObjectQueryBlocker> blockers) => AttachRecursively(control, blockers);

    private static void AttachRecursively(Control control, List<ObjectQueryBlocker> blockers)
    {
        blockers.Add(new ObjectQueryBlocker(control));
        control.ControlAdded += (_, eventArgs) =>
        {
            if (eventArgs.Control != null) AttachRecursively(eventArgs.Control, blockers);
        };
        foreach (Control child in control.Controls) AttachRecursively(child, blockers);
    }

    protected override void WndProc(ref Message message)
    {
        if (NativeAccessibility.IsObjectQuery(message))
        {
            message.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref message);
    }
}

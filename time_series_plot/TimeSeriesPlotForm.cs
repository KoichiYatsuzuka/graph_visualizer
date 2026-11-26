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

// TimeSeriesPlotForm — 時系列の線と最新の点（赤）を ScottPlot の FormsPlot で描くフォーム。作ったスレッド（UI スレッド）からだけ触る
//
// FormsPlot の操作（左ドラッグで移動・右ドラッグで拡大縮小・ホイールで拡大縮小・中ドラッグで範囲を選んで拡大・
// 矢印キーで移動）はそのまま使える。
//   追従: ふだんは描き直すたびに軸の範囲を全点に合わせる（最新の点を追う）。利用者が上の操作で範囲を変えると追従を止め、
//         その範囲のまま描き続ける（窓のタイトルに paused と出す）。ダブルクリックか中クリックで追従に戻る。
//   範囲が変わったことは、前回合わせた範囲と今の範囲を比べて見つける（操作の種類によらない）。
// ScottPlot の標準の右クリックのメニュー（画像の保存・クリップボードへの写し）・ダブルクリックのベンチマーク表示・
// 中クリック / キーボードの自動調整は外す。メニューの保存のダイアログ・クリップボードは COM を使い、NativeAOT では動かない見込みのため
// （未確認）。中クリックとダブルクリックは「追従に戻る」に置き換える。
//
// メンバー:
//   RedrawInterval: int            — 描き直しを確かめる間隔 [ms]
//   RedrawRequested: Action?       — RedrawInterval ごとに UI スレッドで呼ぶ（TimeSeriesPlot が書き換えを描く）
//   _baseTitle: string             — 窓のタイトル（追従を止めているときは後ろに PausedTitleSuffix を足す）
//   _formsPlot: FormsPlot          — ScottPlot のコントロール（図は _formsPlot.Plot）
//   _orderedX, _orderedY: double[] — 古い順に並べた点（_trace が参照する配列。中身だけ書き換える）
//   _trace: Scatter                — 古い順につないだ線
//   _newestMarker: Marker          — 最新の点（赤）
//   _followsLatest: bool           — 描き直すたびに軸の範囲を全点に合わせるか
//   _limitsAfterAutoScale: AxisLimits? — 最後に合わせた軸の範囲（利用者が変えたかを見つけるため）
//   _redrawTimer: Timer            — RedrawRequested を呼ぶタイマー
//   _objectQueryBlockers: List<ObjectQueryBlocker> — FormsPlot とその子の窓の WM_GETOBJECT を止めるもの（GC されないよう持つ）
//   IsFollowingLatest: bool        — 追従しているか
//   SetAxisLabels(xLabel, yLabel)  — 軸の名前
//   CopyPointsOldestFirst(xValues, yValues, newestIndex) — 輪の配列を古い順に並べて写し、赤い点を最新の点へ動かす
//   RenderPlot()                   — （追従していれば軸の範囲を点に合わせて）窓に描き直す
//   FollowLatest()                 — 追従に戻って描き直す
//   SavePng(path, width, height)   — 今の図（今の軸の範囲）を PNG で保存する
//
// 例（UI スレッドで）:
//   var form = new TimeSeriesPlotForm("potential history", 300);
//   form.CopyPointsOldestFirst(xValues, yValues, newestIndex);
//   form.RenderPlot();
//   form.Show();
//
// ⚠ NativeAOT の WinForms では UI Automation が動かないので、フォームは WndProc で、FormsPlot とその子の窓
//   （TransparentSKControl）は ObjectQueryBlocker で WM_GETOBJECT を止める（下の NativeAccessibility を参照）。
internal sealed class TimeSeriesPlotForm : Form
{
    private const int RedrawInterval = 33;
    private const int InitialClientWidth = 640;
    private const int InitialClientHeight = 360;
    private const float TraceLineWidth = 2.0f;
    private const float NewestMarkerSize = 10.0f;
    private const string PausedTitleSuffix = " — paused (double-click to follow the latest points)";
    private static readonly ScottPlot.Color TraceColor = ScottPlot.Color.FromHex("#1f77b4");
    private static readonly ScottPlot.Color NewestMarkerColor = ScottPlot.Color.FromHex("#e6202a");

    private readonly string _baseTitle;
    private readonly FormsPlot _formsPlot;
    private readonly double[] _orderedX;
    private readonly double[] _orderedY;
    private readonly ScottPlot.Plottables.Scatter _trace;
    private readonly ScottPlot.Plottables.Marker _newestMarker;
    private readonly System.Windows.Forms.Timer _redrawTimer;
    private readonly List<ObjectQueryBlocker> _objectQueryBlockers = new();
    private bool _followsLatest = true;
    private ScottPlot.AxisLimits? _limitsAfterAutoScale;

    public Action? RedrawRequested;

    // TimeSeriesPlotForm — 点がすべて (0, 0) の図のフォームを作る。表示はしない（Show は呼んだ側が決める）
    //   title:    窓のタイトル
    //   capacity: 点の数（1 以上）
    //   例: var form = new TimeSeriesPlotForm("potential history", 300);
    public TimeSeriesPlotForm(string title, int capacity)
    {
        _baseTitle = title;
        Text = title;
        ClientSize = new Size(InitialClientWidth, InitialClientHeight);

        SkiaNativeLoader.EnsureLoaded();
        _formsPlot = new FormsPlot { Dock = DockStyle.Fill };
        _orderedX = new double[capacity];
        _orderedY = new double[capacity];
        _trace = _formsPlot.Plot.Add.ScatterLine(_orderedX, _orderedY, TraceColor);
        _trace.LineWidth = TraceLineWidth;
        _newestMarker = _formsPlot.Plot.Add.Marker(0.0, 0.0, ScottPlot.MarkerShape.FilledCircle, NewestMarkerSize, NewestMarkerColor);
        ConfigureUserInput();
        Controls.Add(_formsPlot);
        ObjectQueryBlocker.AttachToTree(_formsPlot, _objectQueryBlockers);

        _redrawTimer = new System.Windows.Forms.Timer { Interval = RedrawInterval };
        _redrawTimer.Tick += (_, _) => RedrawRequested?.Invoke();
        _redrawTimer.Start();
        FormClosed += (_, _) => _redrawTimer.Stop();
    }

    // IsFollowingLatest — 描き直すたびに軸の範囲を全点に合わせているか（利用者が範囲を変えると false）
    //   例: bool following = form.IsFollowingLatest;
    public bool IsFollowingLatest => _followsLatest;

    // WndProc — WM_GETOBJECT だけ WinForms の UI Automation を通さずに OS へ渡す（NativeAccessibility を参照）
    protected override void WndProc(ref Message message)
    {
        if (NativeAccessibility.IsObjectQuery(message)) { DefWndProc(ref message); return; }
        base.WndProc(ref message);
    }

    // SetAxisLabels — 軸の名前を決める（次の RenderPlot から出る）
    //   例: form.SetAxisLabels("Period", "E / V");
    public void SetAxisLabels(string xLabel, string yLabel)
    {
        _formsPlot.Plot.XLabel(xLabel);
        _formsPlot.Plot.YLabel(yLabel);
    }

    // CopyPointsOldestFirst — 輪のように使う配列の点を、最も古い点（newestIndex の次）から最新の点の順に並べて写し、
    //   赤い点を最新の点へ動かす
    //   xValues, yValues: 輪の配列（長さは capacity。書き換えない）
    //   newestIndex:      最新の点の番号
    //   例: form.CopyPointsOldestFirst(xValues, yValues, 4);   // 番号 5, 6, …, capacity - 1, 0, …, 4 の順
    public void CopyPointsOldestFirst(double[] xValues, double[] yValues, int newestIndex)
    {
        int capacity = _orderedX.Length;
        int oldestIndex = (newestIndex + 1) % capacity;
        for (int orderedIndex = 0; orderedIndex < capacity; orderedIndex++)
        {
            int ringIndex = (oldestIndex + orderedIndex) % capacity;
            _orderedX[orderedIndex] = xValues[ringIndex];
            _orderedY[orderedIndex] = yValues[ringIndex];
        }
        _newestMarker.Location = new ScottPlot.Coordinates(xValues[newestIndex], yValues[newestIndex]);
    }

    // RenderPlot — 窓に描き直す。追従していれば、先に軸の範囲を今の点に合わせる。
    //   前回合わせた後に利用者が範囲を変えていれば（FormsPlot の操作）、追従を止めてその範囲のまま描く
    //   例: form.RenderPlot();
    public void RenderPlot()
    {
        if (_followsLatest && _limitsAfterAutoScale is ScottPlot.AxisLimits limitsWeSet && !limitsWeSet.Equals(_formsPlot.Plot.Axes.GetLimits()))
            SetFollowing(false);
        if (_followsLatest)
        {
            _formsPlot.Plot.Axes.AutoScale();
            _limitsAfterAutoScale = _formsPlot.Plot.Axes.GetLimits();
        }
        _formsPlot.Refresh();
    }

    // FollowLatest — 追従に戻り、軸の範囲を今の点に合わせて描き直す（ダブルクリック・中クリックで呼ばれる）
    //   例: form.FollowLatest();
    public void FollowLatest()
    {
        SetFollowing(true);
        _limitsAfterAutoScale = null;
        RenderPlot();
    }

    // SavePng — 今の図（今の軸の範囲。追従していれば最後に描いたときに点に合わせた範囲）を width × height ピクセルの PNG で
    //   path へ保存する（窓の大きさ・表示によらない）
    //   例: form.SavePng("potential_history.png", 800, 450);
    public void SavePng(string path, int width, int height)
    {
        var imageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKSurface surface = SKSurface.Create(imageInfo);
        _formsPlot.Plot.Render(surface.Canvas, width, height);
        using SKImage image = surface.Snapshot();
        using SKData pngData = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, pngData.ToArray());
    }

    // ConfigureUserInput — FormsPlot の標準の操作から、COM を使う見込みのもの・追従と食い違うものを外し、
    //   ダブルクリック（左）と中クリックを「追従に戻る」にする
    private void ConfigureUserInput()
    {
        UserInputProcessor inputProcessor = _formsPlot.UserInputProcessor;
        inputProcessor.RemoveAll<SingleClickContextMenu>();
        inputProcessor.RemoveAll<DoubleClickBenchmark>();
        inputProcessor.RemoveAll<SingleClickAutoscale>();
        inputProcessor.RemoveAll<KeyboardAutoscale>();
        inputProcessor.UserActionResponses.Add(new DoubleClickResponse(StandardMouseButtons.Left, (_, _) => FollowLatest()));
        inputProcessor.UserActionResponses.Add(new SingleClickResponse(StandardMouseButtons.Middle, (_, _) => FollowLatest()));
    }

    // SetFollowing — 追従するかを変え、窓のタイトルに出す
    private void SetFollowing(bool followsLatest)
    {
        if (_followsLatest == followsLatest) return;
        _followsLatest = followsLatest;
        Text = followsLatest ? _baseTitle : _baseTitle + PausedTitleSuffix;
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
// 例: ObjectQueryBlocker.AttachToTree(_formsPlot, _objectQueryBlockers);
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

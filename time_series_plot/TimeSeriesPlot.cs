using System;
using System.Threading;
using System.Windows.Forms;

namespace TimeSeriesPlotBridge;

// TimeSeriesPlot — 輪のように使う固定長の時系列を ScottPlot（FormsPlot）で描く WinForms の窓。Arrow から import[cs-dll] で使う
// （Arrow の型スタブに出る唯一のクラス）
//
// 窓は専用の STA スレッドで動く（Application の message loop）。Arrow のメインスレッドは SetPoint で値の写しを
// 書き換えるだけで、UI スレッドを待たない（Invoke しない）。UI スレッドは RedrawInterval ごとに、書き換えがあれば
// 写しを古い順に並べ替えて描き直す。Arrow から使うときは time_series_plot/__init__.ar の TimeSeriesWindow を通す。
//
// 点の並び（Arrow 側の RingSeries と同じ）:
//   長さ capacity の配列を輪のように使う。最新の点の番号 newestIndex の次（newestIndex + 1、最後なら 0）が最も古い点。
//   最初はすべて (0.0, 0.0) で、newestIndex は capacity - 1（番号 0 から書き始める）。
//   描くときは最も古い点から最新の点まで線でつなぎ、最新の点だけ赤い点で強調する。
//
// メンバー:
//   _form: TimeSeriesPlotForm   — 窓（UI スレッドだけで触る。ほかのスレッドからは Invoke で頼む）
//   _uiThread: Thread           — 窓の message loop を回す STA スレッド（背景スレッド）
//   _pointsLock: object         — _xValues / _yValues / _newestIndex / _hasUnrenderedChange / _uiThreadError を守る lock
//   _xValues, _yValues: double[] — 点の写し（番号は Arrow 側と同じ。Arrow のスレッドが書き、UI スレッドが読む）
//   _newestIndex: int           — 最新の点の番号
//   _hasUnrenderedChange: bool  — まだ描いていない書き換えがあるか
//   _uiThreadError: Exception?  — UI スレッドで捕まえた最初の例外。以後の呼び出しはこれを理由に投げる
//   _isOpen: bool               — 窓が開いているか（利用者が閉じても Close でも false になる）
//   TimeSeriesPlot(title, capacity, visible) — 窓を作る（visible が false なら表示しない・テスト用）
//   SetAxisLabels(xLabel, yLabel) — 軸の名前を決める
//   SetPoint(index, x, y)       — 番号 index の点を (x, y) にし、それを最新の点にする（待たずに戻る）
//   XAt(index), YAt(index) -> double — 写しの点の値（テスト用）
//   NewestIndex() -> long, Capacity() -> long — 最新の点の番号・点の数
//   RenderNow()                 — 書き換えを今すぐ描く（描き終わるまで待つ）
//   IsFollowingLatest() -> bool — 軸の範囲を最新の点に合わせ続けているか（利用者が図を動かすと false。ダブルクリックで true に戻る）
//   SavePng(path, width, height) — 今の点を width × height の PNG に描いて保存する（描き終わるまで待つ）
//   IsOpen() -> bool, Close()   — 窓が開いているか・窓を閉じる
//
// 例（Arrow から・ふつうは TimeSeriesWindow を使う）:
//   import[cs-dll] .TimeSeriesPlotBridge as bridge
//   let plot = bridge.TimeSeriesPlot("potential history", 300, True)
//   plot.SetPoint(0, 0.0167, 0.0012)
public sealed class TimeSeriesPlot
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly TimeSeriesPlotForm _form;
    private readonly Thread _uiThread;
    private readonly object _pointsLock = new();
    private readonly double[] _xValues;
    private readonly double[] _yValues;
    private int _newestIndex;
    private bool _hasUnrenderedChange;
    private Exception? _uiThreadError;
    private volatile bool _isOpen;

    // TimeSeriesPlot — 専用の STA スレッドで窓を作り、窓ができるまで待って戻る
    //   title:    窓のタイトル
    //   capacity: 点の数（1 以上）。Arrow 側の RingSeries の容量と同じにする
    //   visible:  true なら表示する。false なら表示しない（窓は作るので、点を書く・描く・保存することはできる）
    //   capacity が 1 未満なら ArgumentOutOfRangeException、窓が作れなければ InvalidOperationException（中に元の例外）。
    //   例: var plot = new TimeSeriesPlot("potential history", 300, true);
    public TimeSeriesPlot(string title, long capacity, bool visible)
    {
        if (capacity < 1 || capacity > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "capacity must be 1 or more");
        _xValues = new double[capacity];
        _yValues = new double[capacity];
        _newestIndex = (int)capacity - 1;
        _hasUnrenderedChange = true;

        TimeSeriesPlotForm? createdForm = null;
        Exception? startupError = null;
        using var formReady = new ManualResetEventSlim(false);
        _uiThread = new Thread(() =>
        {
            try
            {
                createdForm = CreateForm(title, (int)capacity, visible);
            }
            catch (Exception error)
            {
                startupError = error;
                return;
            }
            finally
            {
                formReady.Set();
            }
            Application.Run(new ApplicationContext());
        })
        {
            IsBackground = true,
            Name = "TimeSeriesPlot UI",
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
        formReady.Wait();
        if (startupError != null || createdForm == null)
            throw new InvalidOperationException("could not create the time series plot window", startupError);
        _form = createdForm;
    }

    // CreateForm — UI スレッドで窓を作る（ハンドルも作るので、表示しなくても Invoke できる）。閉じたら message loop を終える
    //   UI スレッドの例外は WinForms の例外ダイアログ（NativeAOT では出せない）に回さず、RecordUiThreadError で記録する。
    private TimeSeriesPlotForm CreateForm(string title, int capacity, bool visible)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException, threadScope: true);
        Application.ThreadException += (_, eventArgs) => RecordUiThreadError(eventArgs.Exception);
        Application.EnableVisualStyles();
        var form = new TimeSeriesPlotForm(title, capacity);
        form.RedrawRequested = RedrawIfChanged;
        form.FormClosed += (_, _) =>
        {
            _isOpen = false;
            Application.ExitThread();
        };
        _ = form.Handle;
        _isOpen = true;
        if (visible) form.Show();
        return form;
    }

    // SetAxisLabels — 軸の名前を決める（次に描くときから使う）
    //   xLabel: 横軸の名前、yLabel: 縦軸の名前
    //   窓が閉じていれば InvalidOperationException。
    //   例: plot.SetAxisLabels("Period", "E / V");
    public void SetAxisLabels(string xLabel, string yLabel)
    {
        RunOnUiThread(() =>
        {
            _form.SetAxisLabels(xLabel, yLabel);
            return 0L;
        });
        lock (_pointsLock)
        {
            _hasUnrenderedChange = true;
        }
    }

    // SetPoint — 番号 index の点を (x, y) にし、それを最新の点（赤い点）にする。UI スレッドを待たずに戻り、
    //   次の描き直し（RedrawInterval ごと）で窓に出る。窓が閉じた後も写しは書き換わる（描かないだけ）
    //   index: 点の番号（0 以上 capacity 未満）
    //   x, y:  点の横軸・縦軸の値
    //   index が範囲外なら ArgumentOutOfRangeException、UI スレッドが例外で壊れていれば InvalidOperationException。
    //   例: plot.SetPoint(0, 0.0167, 0.0012);
    public void SetPoint(long index, double x, double y)
    {
        lock (_pointsLock)
        {
            ThrowIfUiThreadFailed();
            int checkedIndex = CheckedIndex(index, _xValues.Length);
            _xValues[checkedIndex] = x;
            _yValues[checkedIndex] = y;
            _newestIndex = checkedIndex;
            _hasUnrenderedChange = true;
        }
    }

    // XAt / YAt — 写しの番号 index の点の横軸・縦軸の値（テスト用）
    //   例: double latestX = plot.XAt(plot.NewestIndex());
    public double XAt(long index)
    {
        lock (_pointsLock)
        {
            return _xValues[CheckedIndex(index, _xValues.Length)];
        }
    }

    public double YAt(long index)
    {
        lock (_pointsLock)
        {
            return _yValues[CheckedIndex(index, _yValues.Length)];
        }
    }

    // NewestIndex — 最新の点の番号（最初は capacity - 1）
    //   例: long newest = plot.NewestIndex();
    public long NewestIndex()
    {
        lock (_pointsLock)
        {
            return _newestIndex;
        }
    }

    // Capacity — 点の数
    //   例: long count = plot.Capacity();
    public long Capacity() => _xValues.Length;

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

    // IsFollowingLatest — 軸の範囲を最新の点に合わせ続けているか。利用者が図を動かす・拡大縮小すると false になり、
    //   ダブルクリック・中クリックで true に戻る（TimeSeriesPlotForm を参照）
    //   窓が閉じていれば・UI スレッドが例外で壊れていれば InvalidOperationException。
    //   例: bool following = plot.IsFollowingLatest();
    public bool IsFollowingLatest() => RunOnUiThread(() => _form.IsFollowingLatest ? 1L : 0L) != 0L;

    // SavePng — 今の点を width × height ピクセルの PNG に描いて path へ保存する（窓の大きさ・表示によらない）
    //   path:  保存先のファイル（あれば上書きする）
    //   width, height: 画像の大きさ [pixel]（1 以上）
    //   大きさが 1 未満なら ArgumentOutOfRangeException、窓が閉じていれば InvalidOperationException。
    //   例: plot.SavePng("potential_history.png", 800, 450);
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
    public bool IsOpen() => _isOpen;

    // Close — 窓を閉じ、UI スレッドが終わるのを待つ（最大 CloseTimeout）。閉じた後に呼んでも何もしない
    //   例: plot.Close();
    public void Close()
    {
        if (_isOpen)
        {
            try
            {
                _form.Invoke(() =>
                {
                    _form.Close();
                    Application.ExitThread();
                });
            }
            catch (ObjectDisposedException)
            {
                // Invoke の直前に利用者が閉じた。閉じたい状態にはなっている
            }
            catch (InvalidOperationException) when (!_isOpen || !_form.IsHandleCreated)
            {
                // 同上（ハンドルが壊された後の Invoke）
            }
            _isOpen = false;
        }
        _uiThread.Join(CloseTimeout);
    }

    // RedrawIfChanged — （UI スレッド）まだ描いていない書き換えがあれば、写しを古い順に並べて窓に描き直す
    private void RedrawIfChanged()
    {
        lock (_pointsLock)
        {
            if (!_hasUnrenderedChange) return;
            _form.CopyPointsOldestFirst(_xValues, _yValues, _newestIndex);
            _hasUnrenderedChange = false;
        }
        _form.RenderPlot();
    }

    // RunOnUiThread — action を UI スレッドで実行して結果を返す（例外は呼んだスレッドへ投げ直される）。
    //   窓が閉じていれば・UI スレッドが例外で壊れていれば InvalidOperationException
    private long RunOnUiThread(Func<long> action)
    {
        lock (_pointsLock)
        {
            ThrowIfUiThreadFailed();
        }
        if (!_isOpen) throw new InvalidOperationException("the time series plot window is closed");
        long result = _form.Invoke(action);
        lock (_pointsLock)
        {
            ThrowIfUiThreadFailed();
        }
        return result;
    }

    // RecordUiThreadError — UI スレッドで捕まえた例外を記録し（最初の 1 つ）、標準エラーにも出す
    private void RecordUiThreadError(Exception error)
    {
        lock (_pointsLock)
        {
            _uiThreadError ??= error;
        }
        Console.Error.WriteLine($"[TimeSeriesPlot] error on the window thread: {error}");
    }

    // ThrowIfUiThreadFailed — UI スレッドで例外が起きていれば、それを中に入れた InvalidOperationException を投げる（_pointsLock の中で呼ぶ）
    private void ThrowIfUiThreadFailed()
    {
        if (_uiThreadError != null)
            throw new InvalidOperationException($"the time series plot window thread failed: {_uiThreadError.GetType().FullName}: {_uiThreadError.Message}", _uiThreadError);
    }

    // CheckedIndex — Arrow の int（C# の long）の点の番号を確かめて int にする。範囲外なら ArgumentOutOfRangeException
    private static int CheckedIndex(long index, int capacity)
    {
        if (index < 0 || index >= capacity)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"point index must be 0..{capacity - 1}");
        return (int)index;
    }
}

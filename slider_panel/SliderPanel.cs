using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace SliderPanelBridge;

// SliderPanel — スライダーを並べた WinForms の窓。Arrow から import[cs-dll] で使う（Arrow の型スタブに出る唯一のクラス）
//
// 窓は専用の STA スレッドで動く（Application の message loop）。Arrow のメインスレッドはこのクラスのメソッドを
// 呼ぶだけで止まらない。スライダーが動くと、AddSlider で渡した Arrow の Signal を発火する
// （ArrowEventBridge.Fire・ペイロードは新しい値を "R" 書式にした str）。Arrow 側は EventLoop.run(0.0) を
// 呼んだときにハンドラが走る。Arrow から使うときは slider_panel/__init__.ar の SliderWindow を通す。
//
// メンバー:
//   _form: SliderPanelForm     — 窓（UI スレッドだけで触る。ほかのスレッドからは Invoke で頼む）
//   _uiThread: Thread          — 窓の message loop を回す STA スレッド（背景スレッド）
//   _valuesLock: object        — _sliderValues / _signalIds を守る lock
//   _sliderValues: List<double> — スライダーごとの今の値（UI スレッドが書き、どのスレッドからでも読める写し）
//   _signalIds: List<long>     — スライダーごとに発火する Arrow の Signal の external_id（0 なら発火しない）
//   _uiThreadError: Exception? — UI スレッドで捕まえた最初の例外（_valuesLock で守る）。以後の呼び出しはこれを理由に投げる
//   _isOpen: bool              — 窓が開いているか（利用者が閉じても Close でも false になる）
//   SliderPanel(title, visible) — 窓を作る（visible が false なら表示しない・テスト用）
//   AddSlider(label, minimum, maximum, initial, stepCount, signalId) -> long — スライダーを足して番号を返す
//   GetValue(index) -> double  — スライダーの今の値
//   MoveSlider(index, value)   — 利用者が動かしたのと同じくスライダーを動かす（Signal も発火する）
//   SliderCount() -> long      — スライダーの本数
//   IsOpen() -> bool           — 窓が開いているか
//   Close()                    — 窓を閉じて UI スレッドを終える（何度呼んでもよい）
//
// 例（Arrow から・ふつうは SliderWindow を使う）:
//   import[cs-dll] slider_panel.SliderPanelBridge as bridge
//   let panel = bridge.SliderPanel("controls", True)
//   let index = panel.AddSlider("curvature", -0.01, 0.01, 0.005, 1000, changed.external_id)
public sealed class SliderPanel
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly SliderPanelForm _form;
    private readonly Thread _uiThread;
    private readonly object _valuesLock = new();
    private readonly List<double> _sliderValues = new();
    private readonly List<long> _signalIds = new();
    private Exception? _uiThreadError;
    private volatile bool _isOpen;

    // SliderPanel — 専用の STA スレッドで窓を作り、窓ができるまで待って戻る
    //   title:   窓のタイトル
    //   visible: true なら表示する。false なら表示しない（窓は作るので、スライダーを足す・動かすことはできる）
    //   窓が作れなければ InvalidOperationException（中に元の例外）。
    //   例: var panel = new SliderPanel("graph_visualizer controls", true);
    public SliderPanel(string title, bool visible)
    {
        SliderPanelForm? createdForm = null;
        Exception? startupError = null;
        using var formReady = new ManualResetEventSlim(false);
        _uiThread = new Thread(() =>
        {
            try
            {
                createdForm = CreateForm(title, visible);
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
            Name = "SliderPanel UI",
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
        formReady.Wait();
        if (startupError != null || createdForm == null)
            throw new InvalidOperationException("could not create the slider panel window", startupError);
        _form = createdForm;
    }

    // CreateForm — UI スレッドで窓を作る（ハンドルも作るので、表示しなくても Invoke できる）。閉じたら message loop を終える
    //   UI スレッドの例外は WinForms の例外ダイアログ（NativeAOT では出せない）に回さず、RecordUiThreadError で記録する。
    private SliderPanelForm CreateForm(string title, bool visible)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException, threadScope: true);
        Application.ThreadException += (_, eventArgs) => RecordUiThreadError(eventArgs.Exception);
        Application.EnableVisualStyles();
        var form = new SliderPanelForm(title);
        form.SliderMoved = OnSliderMoved;
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

    // AddSlider — スライダーを 1 本足す
    //   label:     スライダーの左に出す名前
    //   minimum:   左端の値（有限で maximum より小さい）
    //   maximum:   右端の値（有限）
    //   initial:   最初の位置の値（minimum 以上 maximum 以下。最も近い刻みに置く。足しただけでは発火しない）
    //   stepCount: minimum から maximum までの刻みの数（1 以上）
    //   signalId:  スライダーが動いたときに発火する Arrow の Signal の external_id（0 なら発火しない）
    //   戻り値: スライダーの番号（足した順に 0, 1, 2, …）
    //   引数が範囲外なら ArgumentException の仲間、窓が閉じていれば InvalidOperationException、
    //   signalId を渡したのに Arrow の外部イベントにつながっていなければ InvalidOperationException。
    //   例: long index = panel.AddSlider("curvature [1/px]", -0.01, 0.01, 0.005, 1000, signalId);
    public long AddSlider(string label, double minimum, double maximum, double initial, int stepCount, long signalId)
    {
        if (signalId < 0)
            throw new ArgumentOutOfRangeException(nameof(signalId), signalId, "signalId must be 0 (no event) or an Arrow Signal's external_id");
        if (signalId > 0 && !ArrowEventBridge.IsConnected)
            throw new InvalidOperationException("this arrow.exe did not pass ar_event_fire to the bridge, so the slider cannot fire Arrow signals");
        return RunOnUiThread(() =>
        {
            int sliderIndex = _form.AddSlider(label, minimum, maximum, initial, stepCount);
            lock (_valuesLock)
            {
                _sliderValues.Add(_form.ValueOf(sliderIndex));
                _signalIds.Add(signalId);
            }
            return (long)sliderIndex;
        });
    }

    // GetValue — スライダーの今の値（窓を閉じた後も、閉じたときの値を返す）
    //   index: スライダーの番号
    //   例: double curvature = panel.GetValue(0);
    public double GetValue(long index)
    {
        lock (_valuesLock)
        {
            ThrowIfUiThreadFailed();
            return _sliderValues[CheckedIndex(index, _sliderValues.Count)];
        }
    }

    // MoveSlider — スライダーを value に最も近い刻みへ動かす。位置が変われば、利用者が動かしたときと同じく
    //   Signal を発火する（同じ位置なら何も起きない）。戻るときにはイベントは Arrow のキューに積まれている
    //   index: スライダーの番号
    //   value: 動かす先の値（範囲外なら端に寄せる）
    //   例: panel.MoveSlider(0, 0.002);
    public void MoveSlider(long index, double value)
    {
        RunOnUiThread(() =>
        {
            _form.MoveSlider(CheckedIndex(index, _form.SliderCount), value);
            return 0L;
        });
    }

    // SliderCount — スライダーの本数
    //   例: long count = panel.SliderCount();
    public long SliderCount()
    {
        lock (_valuesLock)
        {
            return _sliderValues.Count;
        }
    }

    // IsOpen — 窓が開いているか（利用者が閉じるボタンを押した後・Close の後は false）
    //   例: if (!panel.IsOpen()) { ... }
    public bool IsOpen() => _isOpen;

    // Close — 窓を閉じ、UI スレッドが終わるのを待つ（最大 CloseTimeout）。閉じた後に呼んでも何もしない
    //   例: panel.Close();
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

    // OnSliderMoved — 窓のスライダーが動いたとき（UI スレッド）。値の写しを更新し、Arrow の Signal を発火する
    private void OnSliderMoved(int index, double value)
    {
        long signalId;
        lock (_valuesLock)
        {
            _sliderValues[index] = value;
            signalId = _signalIds[index];
        }
        if (signalId > 0) ArrowEventBridge.Fire(signalId, value.ToString("R", CultureInfo.InvariantCulture));
    }

    // RunOnUiThread — action を UI スレッドで実行して結果を返す（例外は呼んだスレッドへ投げ直される）。
    //   窓が閉じていれば・UI スレッドが例外で壊れていれば InvalidOperationException
    private long RunOnUiThread(Func<long> action)
    {
        lock (_valuesLock)
        {
            ThrowIfUiThreadFailed();
        }
        if (!_isOpen) throw new InvalidOperationException("the slider panel window is closed");
        long result = _form.Invoke(action);
        lock (_valuesLock)
        {
            ThrowIfUiThreadFailed();
        }
        return result;
    }

    // RecordUiThreadError — UI スレッドで捕まえた例外を記録し（最初の 1 つ）、標準エラーにも出す
    private void RecordUiThreadError(Exception error)
    {
        lock (_valuesLock)
        {
            _uiThreadError ??= error;
        }
        Console.Error.WriteLine($"[SliderPanel] error on the window thread: {error}");
    }

    // ThrowIfUiThreadFailed — UI スレッドで例外が起きていれば、それを中に入れた InvalidOperationException を投げる（_valuesLock の中で呼ぶ）
    private void ThrowIfUiThreadFailed()
    {
        if (_uiThreadError != null)
            throw new InvalidOperationException($"the slider panel window thread failed: {_uiThreadError.GetType().FullName}: {_uiThreadError.Message}", _uiThreadError);
    }

    // CheckedIndex — Arrow の int（C# の long）のスライダー番号を確かめて int にする。範囲外なら ArgumentOutOfRangeException
    private static int CheckedIndex(long index, int sliderCount)
    {
        if (index < 0 || index >= sliderCount)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"slider index must be 0..{sliderCount - 1}");
        return (int)index;
    }
}

using System;
using System.Threading;
using System.Windows.Forms;

namespace TimeSeriesPlotBridge;

// UiThreadHost — WinForms のフォームを専用の STA スレッド（Application の message loop）で動かし、ほかのスレッド
// （Arrow のメインスレッド）から UI スレッドへ仕事を頼む仕組み。TimeSeriesPlot と SpectrumPlot が使う
//
// UI スレッドで起きた例外は WinForms の例外ダイアログ（NativeAOT では出せない）に回さず記録し、標準エラーにも出す。
// 以後の Run・ThrowIfFailed はそれを理由に InvalidOperationException を投げる（黙って続けない）。
//
// メンバー:
//   CloseTimeout: TimeSpan     — Close が UI スレッドの終わりを待つ最大の時間
//   _windowDescription: string — 例外のメッセージに出す窓の説明（"time series plot" など）
//   _uiThread: Thread          — message loop を回す STA スレッド（背景スレッド）
//   _errorLock: object         — _uiThreadError を守る lock
//   _uiThreadError: Exception? — UI スレッドで捕まえた最初の例外
//   _isOpen: bool              — 窓が開いているか（利用者が閉じても Close でも false になる）
//   Form: TForm                — 窓（UI スレッドだけで触る。ほかのスレッドからは Run で頼む）
//   UiThreadHost(threadName, windowDescription, createForm, visible) — UI スレッドで createForm を呼んで窓を作り、できるまで待つ
//   IsOpen: bool               — 窓が開いているか
//   Run(action) -> long        — action を UI スレッドで実行して結果を返す（終わるまで待つ）
//   ThrowIfFailed()            — UI スレッドで例外が起きていれば InvalidOperationException を投げる
//   Close()                    — 窓を閉じて UI スレッドが終わるのを待つ（何度呼んでもよい）
//
// 例:
//   var host = new UiThreadHost<SpectrumPlotForm>("SpectrumPlot UI", "spectrum plot", () => new SpectrumPlotForm("spectrum", "nm", "A"), true);
//   host.Run(() => { host.Form.RenderPlot(); return 0L; });
//   host.Close();
internal sealed class UiThreadHost<TForm> where TForm : Form
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly string _windowDescription;
    private readonly Thread _uiThread;
    private readonly object _errorLock = new();
    private Exception? _uiThreadError;
    private volatile bool _isOpen;

    public readonly TForm Form;

    // UiThreadHost — 専用の STA スレッドで createForm を呼んで窓を作り（ハンドルも作るので、表示しなくても Run できる）、
    //   窓ができるまで待って戻る。窓が閉じたら message loop を終える
    //   threadName:        UI スレッドの名前（デバッガに出る）
    //   windowDescription: 例外のメッセージに出す窓の説明
    //   createForm:        UI スレッドで窓を作る関数
    //   visible:           true なら表示する。false なら表示しない（自動テスト用）
    //   窓が作れなければ InvalidOperationException（中に元の例外）。
    public UiThreadHost(string threadName, string windowDescription, Func<TForm> createForm, bool visible)
    {
        _windowDescription = windowDescription;
        TForm? createdForm = null;
        Exception? startupError = null;
        using var formReady = new ManualResetEventSlim(false);
        _uiThread = new Thread(() =>
        {
            try
            {
                createdForm = CreateForm(createForm, visible);
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
            Name = threadName,
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
        formReady.Wait();
        if (startupError != null || createdForm == null)
            throw new InvalidOperationException($"could not create the {windowDescription} window", startupError);
        Form = createdForm;
    }

    // IsOpen — 窓が開いているか（利用者が閉じるボタンを押した後・Close の後は false）
    public bool IsOpen => _isOpen;

    // Run — action を UI スレッドで実行して結果を返す（例外は呼んだスレッドへ投げ直される）
    //   窓が閉じていれば・UI スレッドが例外で壊れていれば InvalidOperationException。
    //   例: long panelIndex = host.Run(() => host.Form.AddPanel(...));
    public long Run(Func<long> action)
    {
        ThrowIfFailed();
        if (!_isOpen) throw new InvalidOperationException($"the {_windowDescription} window is closed");
        long result = Form.Invoke(action);
        ThrowIfFailed();
        return result;
    }

    // ThrowIfFailed — UI スレッドで例外が起きていれば、それを中に入れた InvalidOperationException を投げる
    public void ThrowIfFailed()
    {
        lock (_errorLock)
        {
            if (_uiThreadError != null)
                throw new InvalidOperationException($"the {_windowDescription} window thread failed: {_uiThreadError.GetType().FullName}: {_uiThreadError.Message}", _uiThreadError);
        }
    }

    // Close — 窓を閉じ、UI スレッドが終わるのを待つ（最大 CloseTimeout）。閉じた後に呼んでも何もしない
    public void Close()
    {
        if (_isOpen)
        {
            try
            {
                Form.Invoke(() =>
                {
                    Form.Close();
                    Application.ExitThread();
                });
            }
            catch (ObjectDisposedException)
            {
                // Invoke の直前に利用者が閉じた。閉じたい状態にはなっている
            }
            catch (InvalidOperationException) when (!_isOpen || !Form.IsHandleCreated)
            {
                // 同上（ハンドルが壊された後の Invoke）
            }
            _isOpen = false;
        }
        _uiThread.Join(CloseTimeout);
    }

    // CreateForm — （UI スレッド）例外の受け口を整えてから窓を作り、閉じたら message loop を終えるようにする
    private TForm CreateForm(Func<TForm> createForm, bool visible)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException, threadScope: true);
        Application.ThreadException += (_, eventArgs) => RecordUiThreadError(eventArgs.Exception);
        Application.EnableVisualStyles();
        TForm form = createForm();
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

    // RecordUiThreadError — UI スレッドで捕まえた例外を記録し（最初の 1 つ）、標準エラーにも出す
    private void RecordUiThreadError(Exception error)
    {
        lock (_errorLock)
        {
            _uiThreadError ??= error;
        }
        Console.Error.WriteLine($"[{_windowDescription}] error on the window thread: {error}");
    }
}

using System.Runtime.InteropServices;
using System.Text;

namespace SliderPanelBridge;

// ArrowEventBridge — C# のスレッドから Arrow の Signal を発火する（Arrow の外部イベント・docs/grammar/11_events.md）
//
// arrow.exe は SliderPanelBridge_native.dll を読み込んだ直後に、export の arrow_bridge_set_event_fire へ
// Arrow の ar_event_fire(signal_id, data_ptr, len) の関数ポインタを渡す。Fire はそれを呼んで、Arrow の
// スレッドセーフなキューにイベントを積む。積んだイベントは Arrow 側の EventLoop.run(timeout) が取り出し、
// その Signal の全ハンドラを Arrow のメインスレッドで呼ぶ（ペイロードは str）。
//
// メンバー:
//   _fireEvent: delegate* unmanaged<ulong, byte*, nuint, void> — Arrow の ar_event_fire（注入前は null）
//   SetEventFire(nint)  — arrow.exe が呼ぶ export。_fireEvent を設定する
//   IsConnected: bool   — _fireEvent が設定済みか（外部イベントに対応した arrow.exe から読み込まれたか）
//   Fire(long, string)  — signal_id の Signal へ payload を UTF-8 で発火する
//
// [UnmanagedCallersOnly] のメソッドは Arrow の型スタブに出ない（手書きの export）。
internal static unsafe class ArrowEventBridge
{
    private static delegate* unmanaged<ulong, byte*, nuint, void> _fireEvent;

    // SetEventFire — arrow.exe が DLL の読み込み直後に呼び、ar_event_fire の関数ポインタを渡す
    //   fireFunctionPointer: ar_event_fire のアドレス
    //   例: （Arrow のランタイムが呼ぶ。C# からは呼ばない）
    [UnmanagedCallersOnly(EntryPoint = "arrow_bridge_set_event_fire")]
    public static void SetEventFire(nint fireFunctionPointer)
        => _fireEvent = (delegate* unmanaged<ulong, byte*, nuint, void>)fireFunctionPointer;

    // IsConnected — Arrow の ar_event_fire を受け取り済みなら true（false のときに Fire しても何も届かない）
    //   例: if (!ArrowEventBridge.IsConnected) throw new InvalidOperationException("...");
    internal static bool IsConnected => _fireEvent != null;

    // Fire — Arrow の Signal（sig.external_id が signalId のもの）へ payload を発火する。どのスレッドから呼んでもよい
    //   signalId: Arrow 側で sig.external_id を読んで発番した ID（1 以上）
    //   payload:  ハンドラへ str として渡す文字列（UTF-8 にして渡す）
    //   未接続（IsConnected が false）なら何もしない。
    //   例: ArrowEventBridge.Fire(signalId, value.ToString("R", CultureInfo.InvariantCulture));
    internal static void Fire(long signalId, string payload)
    {
        if (_fireEvent == null) return;
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        fixed (byte* payloadPointer = payloadBytes)
        {
            _fireEvent((ulong)signalId, payloadPointer, (nuint)payloadBytes.Length);
        }
    }
}

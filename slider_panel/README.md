# slider_panel — C#（WinForms）のスライダーの窓から Arrow の mut 変数を書き換える

C# の WinForms で作ったスライダーの窓を Arrow から開き、スライダーを動かすと Arrow の `mut` 変数が書き換わるようにします。
窓は C# の別スレッドで動くので、raylib の描画ループは止まりません。

## 導入（Windows・初回だけ）

前提: .NET SDK 8 以降、Visual Studio 2022（Build Tools 可）の C++ ワークロード（NativeAOT のリンク）、
Arrow（`arrow` に PATH が通っていること）、Arrow のソース（このリポジトリと同じ階層の `test_lang/`。下の「ArPtrProtocol.cs」）。

```powershell
# リポジトリのルートで
powershell -ExecutionPolicy Bypass -File slider_panel/build.ps1      # SliderPanelBridge.dll と SliderPanelBridge_native.dll を作る
powershell -ExecutionPolicy Bypass -File tests/run_tests.ps1         # 自動テスト（窓は表示しない）
arrow main.ar                                                        # raylib の窓とスライダーの窓が開く
```

初回の `build.ps1` は NuGet から NativeAOT のコンパイラ・ランタイムのパッケージを取ってくるので数分かかります
（`SliderPanelBridge_native.dll` は約 57 MB）。DLL・`bin/`・`obj/`・生成した `ArrowExports.g.cs` は `.gitignore` に入れてあり、
別の PC では `build.ps1` で作り直します。

## 使い方

```ar
import slider_panel as sp

mut arrow_curvature = 0.005

# スライダーが動いたら呼ばれる関数。mut 変数へ代入する
fn set_arrow_curvature(let value: float) -> None:
    arrow_curvature = value

mut controls = sp.SliderWindow("graph_visualizer controls")
controls.add_slider("curvature [1/px]", -0.01, 0.01, arrow_curvature, set_arrow_curvature)

while running:
    controls.apply_changes()          # 前のフレームから動いた分だけ set_arrow_curvature が呼ばれる
    ...                               # arrow_curvature を使って描く
controls.close()
```

- `add_slider(label, minimum, maximum, initial, on_change, step_count = 1000)` — スライダーを足して番号（0, 1, …）を返す。
  値は `minimum` から `maximum` を `step_count` 等分した刻みの値だけで、`initial` は最も近い刻みに置く（足しただけでは
  `on_change` は呼ばない）。範囲がおかしければ `ValueError`
- `apply_changes()` — 前回の呼び出しからスライダーが動いた分の `on_change` を、動いた順に呼ぶ。**呼ぶまで変数は変わらない**ので、
  描画ループの各フレームの初めに呼ぶ。中身は `EventLoop.run(0.0)`（ほかの Signal の `emit_async` の分も処理する）
- `get_value(index)` / `move_slider(index, value)` / `slider_count()` / `is_open()` / `close()` — [__init__.ar](__init__.ar) のコメントを参照。
  `move_slider` は利用者がスライダーを動かしたのと同じ経路を通る（自動テストで使う）
- int の変数は `on_change` の中で丸める。整数だけの刻みにするには `step_count` に `maximum - minimum` を渡す:

  ```ar
  mut node_count = 3
  fn set_node_count(let value: float) -> None:
      node_count = int(round(value))
  controls.add_slider("node count", 1.0, 10.0, 3.0, set_node_count, 9)
  ```

- 利用者が窓を閉じても Arrow は止まらない（`is_open()` が `False` になり、以後スライダーの変更は来ない）。

## 仕組み

```
C# の UI スレッド                                    Arrow のメインスレッド
TrackBar.ValueChanged                                 controls.apply_changes()  = EventLoop.run(0.0)
  → SliderPanel.OnSliderMoved                           → 外部イベントのキューから取り出す
  → ArrowEventBridge.Fire(external_id, "0.002")   ⇒      → Signal[str] のハンドラ forward_value("0.002")
    （ar_event_fire がキューに積む）                         → on_change(0.002)  → mut 変数へ代入
```

- Arrow の外部イベント（`sig.external_id` と `ar_event_fire`・test_lang/docs/grammar/11_events.md）を使う。
  arrow.exe は DLL を読み込んだ直後に、export の `arrow_bridge_set_event_fire` へ `ar_event_fire` の関数ポインタを渡す。
- Arrow の変数を書き換えるのは常に Arrow のメインスレッドなので、描画の途中で変数が変わることはない。
- 値は文字列（C# の `"R"` 書式。`1E-05` の形もある）で渡し、Arrow の `float()` で戻す（往復で同じ値になる）。

## ファイル

| ファイル | 役割 |
|---|---|
| [__init__.ar](__init__.ar) | Arrow 側の `SliderWindow`（`import slider_panel as sp`） |
| [SliderPanel.cs](SliderPanel.cs) | Arrow から見える C# のクラス（窓の STA スレッド・値の写し・Signal の発火） |
| [SliderPanelForm.cs](SliderPanelForm.cs) | WinForms のフォーム（スライダー 1 本が 1 行）と位置 ⇔ 値の換算 |
| [ArrowEventBridge.cs](ArrowEventBridge.cs) | `ar_event_fire` を受け取る export と、それを呼ぶ `Fire` |
| [SliderPanelBridge.csproj](SliderPanelBridge.csproj) / [build.ps1](build.ps1) | ビルド（export の生成 → NativeAOT） |

## 注意（NativeAOT の WinForms）

- **UI Automation**: 支援技術や IME が窓に `WM_GETOBJECT` を送ると、WinForms は UI Automation の provider を COM で返そうとし、
  NativeAOT には COM の marshalling が無いので `NotSupportedException` で落ちる。フォームと中のコントロールは
  `WM_GETOBJECT` を OS 標準の処理へ渡す（`SliderPanelForm.cs` の `NativeAccessibility*`）。コントロールを足すときは同じクラスを使う。
- **InvariantGlobalization を付けない**: 付けると WinForms がアセンブリのカルチャ名（`zh-Hans` など）を読んだところで
  `CultureNotFoundException` で落ちる。
- UI スレッドで起きた例外は記録して標準エラーに出し、以後の Arrow からの呼び出しを `ForeignError` にする（黙って続けない）。

## ArPtrProtocol.cs

`arrow --gen-cs-exports` が作る `ArrowExports.g.cs` は、配列を受け渡すメソッドが無くても `ArrowInterop.ArPtrProtocol` を参照します。
その定義 `ArPtrProtocol.cs` は Arrow の配布物に入っていないので、Arrow のソースのものを写さずに参照します
（既定は `../test_lang/bridge/ptr_protocol/ArPtrProtocol.cs`。別の場所なら `build.ps1 -ArPtrProtocolPath <パス>`）。

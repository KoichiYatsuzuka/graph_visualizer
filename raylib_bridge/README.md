# raylib_bridge — Arrow から raylib で描画するための中継 DLL

Arrow の `import[cpp-dll]` で raylib（5.5）を呼ぶための C++ の DLL です。
raylib の関数を snake_case の名前で包んでいます（`InitWindow` → `init_window`、`DrawCircleV` → `draw_circle_v`）。
引数の意味は [raylib cheatsheet](https://www.raylib.com/cheatsheet/cheatsheet.html) のとおりです。

## 導入（Windows・初回だけ）

前提: Visual Studio 2022（Build Tools 可）の C++ ワークロード、Arrow（`arrow` に PATH が通っていること）、Rust（`cargo`。Arrow が `import[cpp-dll]` の中継に使う）。

```powershell
# リポジトリのルートで
powershell -ExecutionPolicy Bypass -File raylib_bridge/install_raylib.ps1   # raylib 5.5 を third_party/raylib/ へ
powershell -ExecutionPolicy Bypass -File raylib_bridge/build.ps1            # raylib_bridge/raylib_bridge.dll を作る
powershell -ExecutionPolicy Bypass -File tests/run_tests.ps1                # 自動テスト（ウィンドウは非表示）
arrow main.ar                                                               # ウィンドウを開いて描画
```

`raylib_bridge.dll` は raylib を静的にリンクしているので、raylib.dll は要りません。
`third_party/raylib/` と `raylib_bridge.dll` は `.gitignore` に入れてあり、別の PC では上の 2 つのスクリプトで作り直します。

## 使い方

```ar
import[cpp-dll] raylib_bridge.raylib_bridge as rl    # tests/ などサブディレクトリからは ..raylib_bridge.raylib_bridge
import raylib_bridge.raylib_consts as rlc

rl.init_window(800, 600, "title".encode("utf-8"))
rl.set_target_fps(60)
mut running = True
while running:
    rl.begin_drawing()
    rl.clear_background(rl.Color(245, 245, 245, 255))
    rl.draw_circle_v(rl.Vector2(400.0, 300.0), 40.0, rl.Color(230, 41, 55, 255))
    rl.draw_text("hello".encode("utf-8"), 20, 20, 20, rl.Color(40, 40, 40, 255))
    rl.end_drawing()
    running = not rl.window_should_close()
rl.close_window()
```

- 文字列（`const char*`）には `str` ではなく `bytes` を渡します（`"text".encode("utf-8")`）。raylib の既定のフォントは ASCII だけです。
- 色は `rl.Color(r, g, b, a)`、座標は `rl.Vector2(x, y)`（float）。raylib の `RED` などの色の定数はありません。
- フラグ・ログの重さ・キーなどの数値は [raylib_consts.ar](raylib_consts.ar) にあります。
- 使える関数の一覧は [raylib_bridge.h](raylib_bridge.h)。次の 7 つは raylib に無い、この DLL だけの関数です。
  - `draw_filled_circle(center_x, center_y, radius, color)` — 円（座標・半径は絶対 pixel。半径が大きくても縁が滑らか）
  - `draw_curved_arrow(start_x, start_y, end_x, end_y, thickness, start_color, end_color, curvature)` — 2 点を結ぶ矢印。
    色は始点→終点のグラデーション（矢じりは終点の色）、`curvature` は 1/R（1/pixel・0 で直線・正なら進む向きに見て左へ曲がる）。
    2 点を通る円弧が無いほど大きい曲率は半円に切り詰める
  - `draw_curved_arrow_three_colors(start_x, start_y, end_x, end_y, thickness, start_color, middle_color, end_color, curvature)` —
    3 色のグラデーションの矢印（向きを強調したいとき。例: 淡い色 → 鮮やかな色 → 濃い色）。
    軸の始点が `start_color`、軸の長さの半分が `middle_color`、矢じりの付け根が `end_color` で、その間は線形に変わる。
    矢じりは `end_color`。ほかの引数は `draw_curved_arrow` と同じ
  - `draw_curved_arrow_flowing(start_x, start_y, end_x, end_y, thickness, start_color, middle_color, end_color, curvature, flow_phase)` —
    色が始点→矢じりへ流れる矢印（毎フレーム呼ぶ）。軸に `start_color → middle_color → end_color → start_color` の
    1 周分（それぞれ軸の 1/3）が並び、`flow_phase`（単位は周期＝軸 1 本分。小数部だけが効く）だけ矢じり側へずれる。
    1 周を `cycle_seconds` 秒にするなら `flow_phase = rl.get_time() / cycle_seconds`。矢じりは `end_color` のまま
  - `draw_curved_line_flowing(...)` — `draw_curved_arrow_flowing` から矢じりを除いたもの（軸が終点まで届く）。引数は同じ。
    反応の矢印の、基質から反応の中心までの部分に使う
  - `read_screen_pixel(x, y)` — 描いた画素の色を読む（テスト用）
  - `save_screen_image(file_name)` — そのフレームに描いたものを画像（`.png`）に保存する。非表示の窓でも使えるので、窓を出さずに
    見た目を確かめられる（`begin_drawing` と `end_drawing` の間で呼ぶ・書けたら `True`）

## 関数を足すとき

1. [raylib_bridge.h](raylib_bridge.h) に宣言を足す（ASCII のみ・宣言の前にマクロを置かない・値渡しの構造体は数値のフィールドだけ）
2. [raylib_bridge.cpp](raylib_bridge.cpp) に実装を足す（宣言と食い違えばコンパイルエラーになる）
3. [raylib_bridge.def](raylib_bridge.def) に名前を足す（足し忘れると Arrow から呼んだときに見つからない）
4. `build.ps1` でビルドし直し、[tests/test_raylib_bridge.ar](../tests/test_raylib_bridge.ar) に呼び出しを足して `run_tests.ps1`

raylib.h を直接 `import[cpp-dll]` しないのは、Arrow がマクロ（`RLAPI`）を展開しないことと、
raylib.h の構造体のフィールド名 `type`（`AutomationEvent`）で Arrow の中継 DLL（Rust）が作れないためです。

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
- 使える関数の一覧は [raylib_bridge.h](raylib_bridge.h)。次の 3 つは raylib に無い、この DLL だけの関数です。
  - `draw_filled_circle(center_x, center_y, radius, color)` — 円（座標・半径は絶対 pixel。半径が大きくても縁が滑らか）
  - `draw_curved_arrow(start_x, start_y, end_x, end_y, thickness, start_color, end_color, curvature)` — 2 点を結ぶ矢印。
    色は始点→終点のグラデーション（矢じりは終点の色）、`curvature` は 1/R（1/pixel・0 で直線・正なら進む向きに見て左へ曲がる）。
    2 点を通る円弧が無いほど大きい曲率は半円に切り詰める
  - `read_screen_pixel(x, y)` — 描いた画素の色を読む（テスト用）

## 関数を足すとき

1. [raylib_bridge.h](raylib_bridge.h) に宣言を足す（ASCII のみ・宣言の前にマクロを置かない・値渡しの構造体は数値のフィールドだけ）
2. [raylib_bridge.cpp](raylib_bridge.cpp) に実装を足す（宣言と食い違えばコンパイルエラーになる）
3. [raylib_bridge.def](raylib_bridge.def) に名前を足す（足し忘れると Arrow から呼んだときに見つからない）
4. `build.ps1` でビルドし直し、[tests/test_raylib_bridge.ar](../tests/test_raylib_bridge.ar) に呼び出しを足して `run_tests.ps1`

raylib.h を直接 `import[cpp-dll]` しないのは、Arrow がマクロ（`RLAPI`）を展開しないことと、
raylib.h の構造体のフィールド名 `type`（`AutomationEvent`）で Arrow の中継 DLL（Rust）が作れないためです。

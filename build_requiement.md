# graph_visualizer のビルド要件

このリポジトリは、Arrow から呼ぶ **3 つのネイティブ DLL** をビルドしてから使います。DLL と取ってきたライブラリは
`.gitignore` に入っているので、clone した直後の PC では下の手順でビルドし直します。

| ビルドするもの | 中身 | 作るスクリプト | できるファイル |
|---|---|---|---|
| raylib_bridge | raylib 5.5 を Arrow から呼ぶ C++ の中継 DLL | `raylib_bridge/install_raylib.ps1` → `raylib_bridge/build.ps1` | `raylib_bridge/raylib_bridge.dll`（約 0.5 MB） |
| slider_panel | スライダーの窓（C# WinForms・NativeAOT） | `slider_panel/build.ps1` | `slider_panel/SliderPanelBridge.dll`（約 26 KB）・`slider_panel/SliderPanelBridge_native.dll`（約 57 MB） |
| time_series_plot | 時系列の窓（C# WinForms + ScottPlot 5 の FormsPlot・.NET 10 の NativeAOT） | `time_series_plot/build.ps1` | `time_series_plot/TimeSeriesPlotBridge.dll`（約 30 KB）・`TimeSeriesPlotBridge_native.dll`（約 21 MB）・`libSkiaSharp.dll`（約 11 MB）・`libHarfBuzzSharp.dll`（約 2 MB） |

対応する OS は **Windows x64 だけ**です（raylib は Windows 用の配布物、窓は WinForms、DLL は win-x64 向け）。

---

## 1. 共通で要るもの

| もの | 要件 | 確かめた版（2026-11-25） | 何に使うか |
|---|---|---|---|
| Windows | 10 / 11 の x64 | Windows 11 Home 10.0.26200 | — |
| Windows PowerShell | 5.1（OS に入っているもの） | 5.1 | ビルド・テストのスクリプト（`powershell -ExecutionPolicy Bypass -File ...`） |
| Visual Studio 2022 | Build Tools でよい。**C++ によるデスクトップ開発**（MSVC v143 x64/x86 と Windows SDK）を入れる | Build Tools 17.12.35527.113・MSVC 14.42.34433・Windows SDK 10.0.22621.0 | raylib_bridge のコンパイル（`cl`）、NativeAOT のリンク、Rust のリンク |
| Arrow | `arrow` に PATH が通っていること。0.7.2 以降 | 0.7.2（test_lang 98366cf7・`C:\Users\koich\repositories\Arrow\arrow.exe`） | 実行・テスト・`--gen-cs-exports`（C# の export の生成）・cs-dll の外部イベント |
| Rust | stable の **MSVC 版**ツールチェーン。`rustc` に PATH が通っていること | rustc 1.95.0（stable-x86_64-pc-windows-msvc） | Arrow が `import[cpp-dll]` の中継 DLL を初回にコンパイルする（`rustc` を直接呼ぶ） |
| ネットワーク | 初回だけ GitHub と nuget.org へ出られること | — | raylib の zip・.NET の NativeAOT のパッケージ |

- `vswhere.exe`（`C:\Program Files (x86)\Microsoft Visual Studio\Installer\`）は Visual Studio と一緒に入ります。
  ビルドスクリプトはこれで Visual Studio を探し、PATH にも足します。
- Arrow は、エントリのファイルのディレクトリと、ヘッダのディレクトリから上にたどった `ar_config.json` を読みます。
  このリポジトリには `ar_config.json` を置いていません（無くてもビルド・実行できることを確かめています）。

---

## 2. raylib_bridge（raylib）

### 要件

| もの | 要件 |
|---|---|
| raylib | **5.5** の公式ビルド済み配布物 `raylib-5.5_win64_msvc16.zip`（GitHub Releases）。`install_raylib.ps1` がダウンロードし、SHA-256 `8D046084D12353183E701EF4C9D276C21FCD3243C2A368091FABFB2769B8507C` を照合してから `third_party/raylib/`（`include/`・`lib/`・`LICENSE`）へ展開する |
| C++ コンパイラ | MSVC（Visual Studio 2022 の x64 ツール）。`build.ps1` が `vcvarsall.bat x64` を呼んで `cl` を使う |
| C++ の規格・設定 | `/std:c++17 /O2 /MD /EHsc /W4 /utf-8`。raylib の配布物は `/MD`（MSVCRT）でビルドされているので、`/MD` は変えない |
| リンクするライブラリ | `raylib.lib`（静的）・`opengl32.lib`・`gdi32.lib`・`winmm.lib`・`user32.lib`・`shell32.lib`（後ろの 5 つは Windows SDK） |
| 実行時 | **OpenGL 3.3** を使える GPU とドライバ（raylib 5.5 のデスクトップの既定。確かめた環境は AMD Radeon で OpenGL 3.3 Core Profile）。`raylib.dll` は要らない（`raylib.lib` を静的にリンク） |

- `msvc16` は Visual Studio 2019 でビルドされた配布物ですが、Visual Studio 2022 の MSVC でそのままリンクできます（MSVC v14x の互換）。
- raylib のライセンスは zlib/libpng ライセンスです（`third_party/raylib/LICENSE`）。
- 版を上げるときは `install_raylib.ps1` の先頭の 3 つ（版・URL・SHA-256）を一緒に書き換えます。

### 手順

```powershell
# リポジトリのルートで
powershell -ExecutionPolicy Bypass -File raylib_bridge/install_raylib.ps1   # raylib 5.5 を third_party/raylib/ へ（-Force で入れ直し）
powershell -ExecutionPolicy Bypass -File raylib_bridge/build.ps1            # raylib_bridge/raylib_bridge.dll を作る
```

`vcvarsall.bat` を自動で見つけられないときは `build.ps1 -VcvarsallPath <vcvarsall.bat のパス>` で渡します。
関数の足し方・ヘッダの書き方の制約は [raylib_bridge/README.md](raylib_bridge/README.md) にあります。

---

## 3. slider_panel（C# WinForms・NativeAOT）

### 要件

| もの | 要件 |
|---|---|
| .NET SDK | **8.0.100 以降**（`net8.0-windows` の NativeAOT と WinForms）。確かめたのは **9.0.101**。WinForms の参照（Microsoft.WindowsDesktop.App）は Windows 版の SDK に入っている |
| ターゲット | `net8.0-windows`・`win-x64`・`PublishAot=true`・`UseWindowsForms=true`・`AllowUnsafeBlocks=true` |
| NuGet のパッケージ | 初回の `build.ps1` が nuget.org から自動で取ってくる（版は SDK が決める。9.0.101 では 8.0.11）: `Microsoft.DotNet.ILCompiler`・`runtime.win-x64.Microsoft.DotNet.ILCompiler`・`Microsoft.NET.ILLink.Tasks`・`Microsoft.NETCore.App.Runtime.win-x64`・`Microsoft.WindowsDesktop.App.Runtime.win-x64`・`Microsoft.AspNetCore.App.Runtime.win-x64`。置き場所は `%USERPROFILE%\.nuget\packages` |
| C++ のツール | NativeAOT は MSVC のリンカと Windows SDK を使う（共通の Visual Studio 2022 Build Tools でよい） |
| Arrow | `arrow --gen-cs-exports` で `ArrowExports.g.cs` を作る。実行時は、DLL の export `arrow_bridge_set_event_fire` に `ar_event_fire` を渡す Arrow（外部イベントに対応した 0.7.2 など）が要る |
| Arrow のソース | `ArPtrProtocol.cs`（生成した `ArrowExports.g.cs` が参照するが、Arrow の配布物に入っていない）。既定はこのリポジトリと同じ階層の `../test_lang/bridge/ptr_protocol/ArPtrProtocol.cs`。別の場所なら `build.ps1 -ArPtrProtocolPath <パス>` |
| 実行時 | **.NET のランタイムは要らない**（NativeAOT の自己完結の DLL）。`SliderPanelBridge.dll`（型スタブ用）と `SliderPanelBridge_native.dll`（実体）の 2 つを `slider_panel/` に置く |

- `InvariantGlobalization` は**付けない**でください。付けると、WinForms が `zh-Hans` などのカルチャ名を読むところで `CultureNotFoundException` が起き、落ちます。
- NativeAOT の WinForms では UI Automation が動かないので、コントロールは `SliderPanelForm.cs` の `NativeAccessibility*` の派生を使います
  （詳しくは [slider_panel/README.md](slider_panel/README.md) の「注意」）。

### 手順

```powershell
# リポジトリのルートで（初回は NuGet の取得を含めて数分）
powershell -ExecutionPolicy Bypass -File slider_panel/build.ps1
```

`build.ps1` は ① `dotnet build`（生成物を外す）→ ② `arrow --gen-cs-exports` → ③ `dotnet publish`（NativeAOT）→ ④ DLL を
`slider_panel/` へ写す、の順に進みます。`arrow` が PATH に無ければ `-ArrowPath <arrow.exe のパス>` で渡します。

### 既知の警告（2026-11-25 時点・未対処）

- **NU1903**: SDK 9.0.101 が選ぶ `Microsoft.DotNet.ILCompiler` 8.0.11 に、高い重大度の既知の脆弱性があります
  （[GHSA-p3jp-9v83-76w9](https://github.com/advisories/GHSA-p3jp-9v83-76w9)）。NativeAOT の DLL にはこの版のランタイムが入ります。
- **.NET 8 のサポート終了**: .NET 8 と .NET 9 のサポートは 2026-11-10 に終わりました
  （[Microsoft の告知](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/)）。後継の .NET 10 は LTS で、2028-11-14 まで
  サポートされます（[.NET のリリースとサポート](https://learn.microsoft.com/dotnet/core/releases-and-support)）。
- 対処の案（未実施・未検証）: .NET 10 SDK を入れ、`slider_panel/SliderPanelBridge.csproj` の `TargetFramework` と
  `slider_panel/build.ps1` の `$TargetFramework` を `net10.0-windows` にしてビルドし直し、`tests/run_tests.ps1` で確かめる。

## 4. time_series_plot（C# WinForms + ScottPlot・NativeAOT）

要件は slider_panel とほぼ同じ（Visual Studio の C++・Arrow・`ArPtrProtocol.cs`）で、違うのは次のもの:

| もの | 要件 |
|---|---|
| .NET SDK | **10.0.100 以降**（確かめたのは 10.0.404。NativeAOT のパッケージは 10.0.15 が選ばれ、NU1903 は出ない） |
| ターゲット | **`net10.0-windows10.0.19041.0`**（`windows10.0.19041` まで書く。下の注意）・`win-x64`・`PublishAot=true` |
| NuGet のパッケージ | **ScottPlot.WinForms 5.1.59**（MIT。csproj で版を固定）と、それが引く ScottPlot・SkiaSharp 3.119.0・SkiaSharp.Views.WindowsForms・HarfBuzzSharp・OpenTK（FormsPlotGL 用。使わない）。初回の `build.ps1` が取ってくる |
| 実行時 | `TimeSeriesPlotBridge.dll`・`TimeSeriesPlotBridge_native.dll` に加え、SkiaSharp のネイティブ DLL `libSkiaSharp.dll`・`libHarfBuzzSharp.dll` を `time_series_plot/` に置く（`build.ps1` が publish の出力から写す。`SkiaNativeLoader.cs` がこのフォルダから先に読み込む） |

- **ターゲットに `windows10.0.19041` まで書く**: FormsPlot が使う SkiaSharp.Views.WindowsForms 3.119.0 は、.NET 用のアセンブリが
  `net8.0-windows10.0.19041` にしか無く、`net*-windows`（= windows7.0）では .NET Framework 用が選ばれて NU1701 になります。
  csproj の `TargetFramework` と `build.ps1` の `$TargetFramework` は同じにします。
- slider_panel と同じく `InvariantGlobalization` は付けません。WM_GETOBJECT は、フォームは WndProc で、派生を作れない FormsPlot の中の
  窓（TransparentSKControl）は `ObjectQueryBlocker`（NativeWindow で割り込む）で止めます（`TimeSeriesPlotForm.cs`）。
- FormsPlot の標準の右クリックのメニュー（画像の保存のダイアログ・クリップボード）は COM を使うので外しています（NativeAOT で動くかは未確認）。
- NativeAOT の DLL が slider_panel（.NET 8）と合わせて 2 つ、同じプロセスに入ります（それぞれ別の版の .NET のランタイムを持つ）。同時に開けることは
  `tests/test_time_series_plot.ar` で確かめています。

```powershell
powershell -ExecutionPolicy Bypass -File time_series_plot/build.ps1
```

---

## 5. 全体の手順と確認

```powershell
# リポジトリのルートで。test_lang（Arrow のソース）が ../test_lang にあること
powershell -ExecutionPolicy Bypass -File raylib_bridge/install_raylib.ps1
powershell -ExecutionPolicy Bypass -File raylib_bridge/build.ps1
powershell -ExecutionPolicy Bypass -File slider_panel/build.ps1
powershell -ExecutionPolicy Bypass -File time_series_plot/build.ps1
powershell -ExecutionPolicy Bypass -File tests/run_tests.ps1    # 自動テスト（窓は表示しない）。「7 / 7 passed」なら成功
arrow main.ar                                                   # raylib の窓・スライダーの窓・電位の時系列の窓が開く
```

| テスト | 確かめること |
|---|---|
| `tests/test_raylib_bridge.ar` | raylib_bridge のすべての関数を呼び、描いた画素を読み戻して色・位置を確かめる |
| `tests/test_slider_panel.ar` | スライダーを動かすと `apply_changes` で Arrow の `mut` 変数が書き換わること、引数の誤り・窓を閉じた後の振る舞い |
| `tests/test_time_series_plot.ar` | 輪のように使う時系列の書く順・一周後の上書き、窓の写しとの一致、ScottPlot で描けて PNG を保存できること、操作しなければ追従が続くこと、スライダーの窓と同時に開けること、窓を閉じた後の振る舞い |

初回の `arrow` の実行では、`import[cpp-dll]` の中継 DLL を `rustc` でコンパイルするので、起動に少し時間がかかります。

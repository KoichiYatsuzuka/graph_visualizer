- C++クラスのコンストラクタを無視してデフォルトコンストラクタを呼ぶ

- ArPtrProtocol.cs が Arrow の配布物に入っていない。

- cpp-dll の構造体を返す関数が import の順で落ちる。同じ DLL を import するモジュール（chemical_graphs）をエントリの `import[cpp-dll]` より先に import すると、エントリで `rl.get_mouse_position()` などを呼んだ時点で `NameError: 'Vector2' is not defined`（`mustbe` の有無は無関係。int を返す関数・構造体を作るだけなら通る）。
  - エントリで DLL を先に import すれば起きない（main.ar・tests/test_chemical_graphs.ar はこの順で回避中）。Arrow 0.7.2・2026-11-26 実測。
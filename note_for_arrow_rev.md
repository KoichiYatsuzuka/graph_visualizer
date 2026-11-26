- C++クラスのコンストラクタを無視してデフォルトコンストラクタを呼ぶ

- ArPtrProtocol.cs が Arrow の配布物に入っていない。

- cpp-dll の構造体を返す関数が import の順で落ちる。同じ DLL を import するモジュール（chemical_graphs）をエントリの `import[cpp-dll]` より先に import すると、エントリで `rl.get_mouse_position()` などを呼んだ時点で `NameError: 'Vector2' is not defined`（`mustbe` の有無は無関係。int を返す関数・構造体を作るだけなら通る）。
  - エントリで DLL を先に import すれば起きない（main.ar・tests/test_chemical_graphs.ar はこの順で回避中）。Arrow 0.7.2・2026-11-26 実測。

- デフォルトコンストラクタの引数リストが表示されない

- 組み込み関数のデフォルト引数の情報がコメントにしかない。

- 関数の中のタプルの分解宣言 `let a, let b = f()` で `VmForceError: cannot compile function '..' to bytecode`（最上位なら通る）。tests/test_kinetics.ar は `t = f()` → `t[0]`・`t[1]` で回避中。Arrow 0.7.2・2026-11-26 実測。

- 関数型の new_type（`new_type Fn_RateCoefficient: function[...]->float`）の値をそのまま呼べない（`'Fn_RateCoefficient' has no method '__call__'`）。`=> function[let Reaction, ...]->float` で戻せば呼べるが、通るのは定義したモジュールの中だけ。外から `=> function[let cg.Reaction, ...]->float` と書くと `not castable`、`.value` で取り出すと `expects 'pkg.pkg.Rx' but got 'Rx'`（モジュール名の修飾の食い違い）。cg.Reaction のメソッドの中で戻して回避中。

- `let` のインスタンスを入れた list を `mut xs = [a, b]` で写すと、写しの要素も不変のまま（`mut self` のメソッドが `cannot call mutable method .. on immutable instance`）。`mut` の写しは可変になると思っていた（main.ar の化学種は mut のままにして回避）。

- and が予約語の色にならない
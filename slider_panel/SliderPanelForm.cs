using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace SliderPanelBridge;

// SliderPanelForm — スライダーを縦に並べるフォーム。作ったスレッド（UI スレッド）からだけ触る
//
// スライダー 1 本が表の 1 行で、[名前のラベル][TrackBar][今の値のラベル] を並べる。
// TrackBar は整数の位置 0..stepCount しか持てないので、位置 p の値を
//   minimum + (maximum - minimum) * p / stepCount
// とする（SliderRow.ValueAt）。
//
// メンバー:
//   SliderMoved: Action<int, double>? — スライダーの値が変わったとき（利用者が動かしても MoveSlider でも）に
//                                       UI スレッドで呼ぶ。引数はスライダーの番号（0 始まり）と新しい値
//   _sliderTable: TableLayoutPanel     — 行を並べる 3 列の表
//   _sliderRows: List<SliderRow>       — 足した順のスライダー（添字がスライダーの番号）
//   SliderCount: int                   — スライダーの本数
//   AddSlider(label, minimum, maximum, initial, stepCount) -> int — 1 行足して番号を返す（SliderMoved は呼ばない）
//   MoveSlider(index, value)           — スライダーを value に最も近い位置へ動かす（位置が変われば SliderMoved を呼ぶ）
//   ValueOf(index) -> double           — スライダーの今の値
//
// 例（UI スレッドで）:
//   var form = new SliderPanelForm("controls");
//   form.SliderMoved = (index, value) => Console.WriteLine($"{index}: {value}");
//   int index = form.AddSlider("curvature", -0.01, 0.01, 0.005, 1000);
//   form.Show();
//
// ⚠ フォームと中のコントロールはすべて NativeAccessibility の WndProc を通す（下の Native* クラス）。
internal sealed class SliderPanelForm : Form
{
    private const int LabelColumnMinimumWidth = 120;
    private const int TrackBarWidth = 300;
    private const int ValueColumnWidth = 96;
    private const int TickCountPerSlider = 10;
    private const string ValueFormat = "G6";

    private readonly TableLayoutPanel _sliderTable;
    private readonly List<SliderRow> _sliderRows = new();

    public Action<int, double>? SliderMoved;

    // SliderPanelForm — 空の（スライダーの無い）フォームを作る。表示はしない（Show は呼んだ側が決める）
    //   title: 窓のタイトル
    //   例: var form = new SliderPanelForm("graph_visualizer controls");
    public SliderPanelForm(string title)
    {
        Text = title;
        Font = SystemFonts.MessageBoxFont ?? Font;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Padding = new Padding(12);
        MinimumSize = new Size(LabelColumnMinimumWidth + TrackBarWidth + ValueColumnWidth, 0);

        _sliderTable = new NativeAccessibilityTableLayoutPanel
        {
            ColumnCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };
        _sliderTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _sliderTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, TrackBarWidth));
        _sliderTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ValueColumnWidth));
        Controls.Add(_sliderTable);
    }

    // SliderCount — スライダーの本数
    //   例: int next_index = form.SliderCount;
    public int SliderCount => _sliderRows.Count;

    // WndProc — WM_GETOBJECT だけ WinForms の UI Automation を通さずに OS へ渡す（NativeAccessibility を参照）
    protected override void WndProc(ref Message message)
    {
        if (NativeAccessibility.IsObjectQuery(message)) { DefWndProc(ref message); return; }
        base.WndProc(ref message);
    }

    // AddSlider — スライダーを 1 行足す。initial に最も近い位置に置き、SliderMoved は呼ばない
    //   label:     行の左に出す名前
    //   minimum:   左端の値（有限で maximum より小さい）
    //   maximum:   右端の値（有限）
    //   initial:   最初の値（minimum 以上 maximum 以下）
    //   stepCount: minimum から maximum までの刻みの数（1 以上）。値の細かさは (maximum - minimum) / stepCount
    //   戻り値: スライダーの番号（足した順に 0, 1, 2, …）
    //   引数が範囲外なら ArgumentException / ArgumentOutOfRangeException を投げ、行は足さない。
    //   例: int index = form.AddSlider("flow cycle [s]", 1.0, 20.0, 6.0, 190);
    public int AddSlider(string label, double minimum, double maximum, double initial, int stepCount)
    {
        SliderRow.Validate(minimum, maximum, initial, stepCount);
        int sliderIndex = _sliderRows.Count;
        var sliderRow = new SliderRow(label, minimum, maximum, stepCount);
        sliderRow.TrackBar.Value = sliderRow.PositionNearest(initial);
        sliderRow.ShowValue();
        sliderRow.TrackBar.ValueChanged += (_, _) => OnTrackBarValueChanged(sliderIndex);

        _sliderTable.RowCount = sliderIndex + 1;
        _sliderTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _sliderTable.Controls.Add(sliderRow.NameLabel, 0, sliderIndex);
        _sliderTable.Controls.Add(sliderRow.TrackBar, 1, sliderIndex);
        _sliderTable.Controls.Add(sliderRow.ValueLabel, 2, sliderIndex);
        _sliderRows.Add(sliderRow);
        return sliderIndex;
    }

    // MoveSlider — スライダーを value に最も近い位置へ動かす。利用者が動かしたときと同じく、位置が変われば
    //   SliderMoved を呼ぶ（同じ位置なら何も起きない）
    //   index: スライダーの番号
    //   value: 動かす先の値（範囲外なら端に寄せる。NaN は ArgumentException）
    //   例: form.MoveSlider(0, 0.002);
    public void MoveSlider(int index, double value)
    {
        if (double.IsNaN(value)) throw new ArgumentException("value must not be NaN", nameof(value));
        SliderRow sliderRow = RowAt(index);
        sliderRow.TrackBar.Value = sliderRow.PositionNearest(value);
    }

    // ValueOf — スライダーの今の値
    //   index: スライダーの番号
    //   例: double curvature = form.ValueOf(0);
    public double ValueOf(int index) => RowAt(index).CurrentValue;

    // RowAt — 番号のスライダーの行。番号が範囲外なら ArgumentOutOfRangeException
    private SliderRow RowAt(int index)
    {
        if (index < 0 || index >= _sliderRows.Count)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"slider index must be 0..{_sliderRows.Count - 1}");
        return _sliderRows[index];
    }

    // OnTrackBarValueChanged — TrackBar の位置が変わったときに、値のラベルを更新して SliderMoved を呼ぶ
    private void OnTrackBarValueChanged(int index)
    {
        SliderRow sliderRow = _sliderRows[index];
        sliderRow.ShowValue();
        SliderMoved?.Invoke(index, sliderRow.CurrentValue);
    }

    // SliderRow — スライダー 1 行分のコントロールと、位置 ⇔ 値の換算
    //
    // メンバー:
    //   NameLabel, ValueLabel: Label — 名前と今の値（ValueFormat の書式）を出すラベル
    //   TrackBar: TrackBar            — 位置 0..StepCount のスライダー
    //   Minimum, Maximum: double      — 左端・右端の値
    //   StepCount: int                — 刻みの数
    //   CurrentValue: double          — 今の位置の値（ValueAt(TrackBar.Value)）
    //   Validate(...)                 — AddSlider の引数を確かめる（範囲外なら例外）
    //   ValueAt(position) -> double   — 位置の値
    //   PositionNearest(value) -> int — value に最も近い位置（範囲外は端）
    //   ShowValue()                   — 値のラベルを今の値にする
    private sealed class SliderRow
    {
        public readonly Label NameLabel;
        public readonly TrackBar TrackBar;
        public readonly Label ValueLabel;
        public readonly double Minimum;
        public readonly double Maximum;
        public readonly int StepCount;

        public SliderRow(string label, double minimum, double maximum, int stepCount)
        {
            Minimum = minimum;
            Maximum = maximum;
            StepCount = stepCount;
            NameLabel = new NativeAccessibilityLabel
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 0, 8, 0),
            };
            int tickSpacing = Math.Max(1, stepCount / TickCountPerSlider);
            TrackBar = new NativeAccessibilityTrackBar
            {
                Minimum = 0,
                Maximum = stepCount,
                TickFrequency = tickSpacing,
                SmallChange = 1,
                LargeChange = tickSpacing,
                TickStyle = TickStyle.BottomRight,
                Width = TrackBarWidth,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
            };
            ValueLabel = new NativeAccessibilityLabel
            {
                AutoSize = false,
                Width = ValueColumnWidth,
                TextAlign = ContentAlignment.MiddleRight,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
            };
        }

        public double CurrentValue => ValueAt(TrackBar.Value);

        // Validate — スライダーの範囲と最初の値を確かめる。おかしければ ArgumentException の仲間を投げる
        public static void Validate(double minimum, double maximum, double initial, int stepCount)
        {
            if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum >= maximum)
                throw new ArgumentException($"minimum ({minimum}) and maximum ({maximum}) must be finite and minimum < maximum");
            if (stepCount < 1)
                throw new ArgumentOutOfRangeException(nameof(stepCount), stepCount, "stepCount must be 1 or more");
            if (!(initial >= minimum && initial <= maximum))
                throw new ArgumentOutOfRangeException(nameof(initial), initial, $"initial must be in [{minimum}, {maximum}]");
        }

        // ValueAt — 位置の値。両端の位置は Minimum / Maximum そのもの
        //   途中は decimal（minimum・maximum を 15 桁の 10 進数として読む）で計算する。double のままだと
        //   -0.01 + 0.02 * 750 / 1000 が 0.004999999999999999 になるが、decimal なら 0.005 になる。
        //   decimal で表せない桁（DecimalLowerLimit 未満・DecimalUpperLimit 超）の範囲は double で計算する。
        public double ValueAt(int position)
        {
            if (position <= 0) return Minimum;
            if (position >= StepCount) return Maximum;
            if (!FitsDecimal(Minimum) || !FitsDecimal(Maximum) || !FitsDecimal(Maximum - Minimum))
                return Minimum + (Maximum - Minimum) * position / StepCount;
            decimal minimumDecimal = (decimal)Minimum;
            decimal maximumDecimal = (decimal)Maximum;
            return (double)(minimumDecimal + (maximumDecimal - minimumDecimal) * position / StepCount);
        }

        private const double DecimalLowerLimit = 1e-12;
        private const double DecimalUpperLimit = 1e15;

        // FitsDecimal — value を decimal にしても桁が落ちないか（0 か、絶対値が DecimalLowerLimit 以上 DecimalUpperLimit 以下）
        private static bool FitsDecimal(double value)
        {
            double magnitude = Math.Abs(value);
            return magnitude == 0.0 || (magnitude >= DecimalLowerLimit && magnitude <= DecimalUpperLimit);
        }

        // PositionNearest — value に最も近い位置（ちょうど中間なら 0 から遠い方）。範囲外は端の位置
        public int PositionNearest(double value)
        {
            double fraction = (value - Minimum) / (Maximum - Minimum);
            double position = Math.Round(fraction * StepCount, MidpointRounding.AwayFromZero);
            return (int)Math.Clamp(position, 0.0, StepCount);
        }

        public void ShowValue() => ValueLabel.Text = CurrentValue.ToString(ValueFormat, CultureInfo.InvariantCulture);
    }
}

// NativeAccessibility — WinForms の UI Automation を NativeAOT で使わないための判定
//
// 支援技術や IME などが窓に WM_GETOBJECT を送ると、WinForms は自分の UI Automation の provider を COM で返そうとする。
// NativeAOT には組み込みの COM の marshalling が無いので、そこで NotSupportedException
// （"COM Interop requires ComWrapper instance registered for marshalling"）が UI スレッドに飛ぶ（2026-11-25 実測）。
// そこで WM_GETOBJECT だけは WinForms の WndProc を通さず DefWndProc（元のウィンドウプロシージャ）へ渡し、
// OS 標準の支援技術向けの代理（TrackBar なら comctl32 のもの）に任せる。
//
// メンバー:
//   WM_GETOBJECT: int — Win32 のメッセージ番号
//   IsObjectQuery(message) -> bool — message が WM_GETOBJECT か
// 例: if (NativeAccessibility.IsObjectQuery(message)) { DefWndProc(ref message); return; }
internal static class NativeAccessibility
{
    private const int WM_GETOBJECT = 0x003D;

    internal static bool IsObjectQuery(Message message) => message.Msg == WM_GETOBJECT;
}

// NativeAccessibilityTableLayoutPanel / NativeAccessibilityLabel / NativeAccessibilityTrackBar —
// WM_GETOBJECT を OS へ渡すだけの TableLayoutPanel / Label / TrackBar（ほかは元のクラスのまま）
internal sealed class NativeAccessibilityTableLayoutPanel : TableLayoutPanel
{
    protected override void WndProc(ref Message message)
    {
        if (NativeAccessibility.IsObjectQuery(message)) { DefWndProc(ref message); return; }
        base.WndProc(ref message);
    }
}

internal sealed class NativeAccessibilityLabel : Label
{
    protected override void WndProc(ref Message message)
    {
        if (NativeAccessibility.IsObjectQuery(message)) { DefWndProc(ref message); return; }
        base.WndProc(ref message);
    }
}

internal sealed class NativeAccessibilityTrackBar : TrackBar
{
    protected override void WndProc(ref Message message)
    {
        if (NativeAccessibility.IsObjectQuery(message)) { DefWndProc(ref message); return; }
        base.WndProc(ref message);
    }
}

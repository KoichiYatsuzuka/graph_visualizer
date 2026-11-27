using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using ScottPlot.WinForms;
using SkiaSharp;

namespace TimeSeriesPlotBridge;

// SpectrumPlotForm — スペクトルのグラフ（ScottPlot の FormsPlot が 1 枚）のフォーム。作ったスレッド（UI スレッド）からだけ触る
//
// スペクトルを 1 本の線で描く（ShowSpectrum の前は軸だけの空のグラフ）。軸の範囲は SetAxisLimits で決めた値
// （決める前は DefaultLimits）で、利用者がマウスで動かした後はダブルクリック・中クリックでその範囲に戻る（PlotInput）。
//
// メンバー:
//   DefaultLimits: AxisLimits       — SetAxisLimits の前の軸の範囲（どちらの軸も 0〜1）
//   RedrawInterval: int             — 描き直しを確かめる間隔 [ms]
//   RedrawRequested: Action?        — RedrawInterval ごとに UI スレッドで呼ぶ（SpectrumPlot が書き換えを描く）
//   _spectrumX, _spectrumY: double[] — 線が参照する配列（点の数が変わったら作り直す）
//   _spectrumTrace: Scatter?        — スペクトルの線（ShowSpectrum の前は null）
//   _redrawTimer: Timer             — RedrawRequested を呼ぶタイマー
//   _formsPlot: FormsPlot           — ScottPlot のコントロール（図は _formsPlot.Plot）
//   _homeLimits: AxisLimits         — ダブルクリック・中クリックで戻る軸の範囲
//   _objectQueryBlockers: List<ObjectQueryBlocker> — FormsPlot とその子の窓の WM_GETOBJECT を止めるもの（GC されないよう持つ）
//   SetAxisLimits(limits)           — 軸の範囲を決めて描き直す
//   ShowSpectrum(wavelengths, absorbances) — スペクトルの線の点を写す（描くのは RenderPlot）
//   RenderPlot()                    — 描き直す
//   SavePng(path, width, height)    — 今の図を PNG で保存する
//
// 例（UI スレッドで）:
//   var form = new SpectrumPlotForm("graph_visualizer spectrum", "Wavelength / nm", "Absorbance");
//   form.SetAxisLimits(new ScottPlot.AxisLimits(200.0, 800.0, 0.0, 1.0));
//   form.Show();
internal sealed class SpectrumPlotForm : Form
{
    private const int InitialClientWidth = 640;
    private const int InitialClientHeight = 400;
    private const int RedrawInterval = 33;
    private const float SpectrumLineWidth = 2.0f;
    private static readonly ScottPlot.AxisLimits DefaultLimits = new(0.0, 1.0, 0.0, 1.0);
    private static readonly ScottPlot.Color SpectrumLineColor = ScottPlot.Color.FromHex("#333333");

    private readonly FormsPlot _formsPlot;
    private readonly List<ObjectQueryBlocker> _objectQueryBlockers = new();
    private readonly System.Windows.Forms.Timer _redrawTimer;
    private ScottPlot.AxisLimits _homeLimits = DefaultLimits;
    private double[] _spectrumX = Array.Empty<double>();
    private double[] _spectrumY = Array.Empty<double>();
    private ScottPlot.Plottables.Scatter? _spectrumTrace;

    public Action? RedrawRequested;

    // SpectrumPlotForm — 軸だけの空のグラフのフォームを作る。表示はしない（Show は呼んだ側が決める）
    //   title: 窓のタイトル、xLabel: 横軸の名前、yLabel: 縦軸の名前
    public SpectrumPlotForm(string title, string xLabel, string yLabel)
    {
        Text = title;
        ClientSize = new Size(InitialClientWidth, InitialClientHeight);
        SkiaNativeLoader.EnsureLoaded();

        _formsPlot = new FormsPlot { Dock = DockStyle.Fill };
        _formsPlot.Plot.XLabel(xLabel);
        _formsPlot.Plot.YLabel(yLabel);
        _formsPlot.Plot.Axes.SetLimits(_homeLimits);
        PlotInput.ReplaceDefaultResponses(_formsPlot, ResetView);
        Controls.Add(_formsPlot);
        ObjectQueryBlocker.AttachToTree(_formsPlot, _objectQueryBlockers);

        _redrawTimer = new System.Windows.Forms.Timer { Interval = RedrawInterval };
        _redrawTimer.Tick += (_, _) => RedrawRequested?.Invoke();
        _redrawTimer.Start();
        FormClosed += (_, _) => _redrawTimer.Stop();
    }

    // WndProc — WM_GETOBJECT だけ WinForms の UI Automation を通さずに OS へ渡す（NativeAccessibility を参照）
    protected override void WndProc(ref Message message)
    {
        if (NativeAccessibility.IsObjectQuery(message)) { DefWndProc(ref message); return; }
        base.WndProc(ref message);
    }

    // SetAxisLimits — 軸の範囲を limits にして描き直す（ダブルクリック・中クリックで戻る範囲もこれにする）
    //   例: form.SetAxisLimits(new ScottPlot.AxisLimits(200.0, 800.0, 0.0, 1.0));
    public void SetAxisLimits(ScottPlot.AxisLimits limits)
    {
        _homeLimits = limits;
        ResetView();
    }

    // ShowSpectrum — スペクトルの線の点を写す（点の数が変わったら線を作り直す。描くのは RenderPlot）
    //   wavelengths, absorbances: 同じ長さの並び（書き換えない）
    //   例: form.ShowSpectrum(new double[] { 200.0, 202.0 }, new double[] { 0.0, 0.1 });
    public void ShowSpectrum(double[] wavelengths, double[] absorbances)
    {
        if (_spectrumTrace == null || _spectrumX.Length != wavelengths.Length)
        {
            if (_spectrumTrace != null) _formsPlot.Plot.Remove(_spectrumTrace);
            _spectrumX = new double[wavelengths.Length];
            _spectrumY = new double[wavelengths.Length];
            _spectrumTrace = _formsPlot.Plot.Add.ScatterLine(_spectrumX, _spectrumY, SpectrumLineColor);
            _spectrumTrace.LineWidth = SpectrumLineWidth;
        }
        Array.Copy(wavelengths, _spectrumX, wavelengths.Length);
        Array.Copy(absorbances, _spectrumY, absorbances.Length);
    }

    // RenderPlot — 描き直す
    //   例: form.RenderPlot();
    public void RenderPlot() => _formsPlot.Refresh();

    // SavePng — 今の図（今の軸の範囲）を width × height ピクセルの PNG で path へ保存する（窓の大きさ・表示によらない）
    //   例: form.SavePng("spectrum.png", 800, 450);
    public void SavePng(string path, int width, int height)
    {
        var imageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKSurface surface = SKSurface.Create(imageInfo);
        _formsPlot.Plot.Render(surface.Canvas, width, height);
        using SKImage image = surface.Snapshot();
        using SKData pngData = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, pngData.ToArray());
    }

    // ResetView — 軸の範囲を _homeLimits に戻して描き直す（ダブルクリック・中クリックで呼ばれる）
    private void ResetView()
    {
        _formsPlot.Plot.Axes.SetLimits(_homeLimits);
        _formsPlot.Refresh();
    }
}

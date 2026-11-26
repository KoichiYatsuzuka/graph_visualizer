using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TimeSeriesPlotBridge;

// SkiaNativeLoader — ScottPlot が描画に使う SkiaSharp のネイティブ DLL（libSkiaSharp.dll・libHarfBuzzSharp.dll）を、
// この DLL（TimeSeriesPlotBridge_native.dll）と同じフォルダから先に読み込んでおく
//
// SkiaSharp は DllImport("libSkiaSharp") で DLL を名前だけで探す。arrow.exe から読み込まれた NativeAOT の DLL では、
// 探す場所がこの DLL のフォルダ（time_series_plot/）になるとは限らないので、フルパスで先に LoadLibrary しておく。
// Windows は同じ名前の DLL が読み込み済みならそれを使うので、以後の DllImport はこのフォルダのものに解決される。
//
// メンバー:
//   NativeLibraryNames: string[] — 先に読み込む DLL の名前
//   _isLoaded: bool              — EnsureLoaded が済んだか
//   EnsureLoaded()               — DLL を読み込む（2 回目以降は何もしない。フォルダに無い DLL は飛ばす）
//   ModuleDirectory() -> string? — この DLL のフォルダ（分からなければ null）
// 例: SkiaNativeLoader.EnsureLoaded();   // ScottPlot.Plot を作る前に UI スレッドで呼ぶ
internal static unsafe class SkiaNativeLoader
{
    private static readonly string[] NativeLibraryNames = { "libSkiaSharp.dll", "libHarfBuzzSharp.dll" };
    private const uint GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x4;
    private const uint GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x2;
    private const int MaxModulePathLength = 32768;

    private static bool _isLoaded;

    // EnsureLoaded — このフォルダの SkiaSharp のネイティブ DLL を読み込む。フォルダに無いものは飛ばす
    //   （そのときは SkiaSharp 自身の探し方に任せる。見つからなければ ScottPlot を使ったところで DllNotFoundException）
    //   例: SkiaNativeLoader.EnsureLoaded();
    internal static void EnsureLoaded()
    {
        if (_isLoaded) return;
        string? moduleDirectory = ModuleDirectory();
        if (moduleDirectory != null)
        {
            foreach (string libraryName in NativeLibraryNames)
            {
                string libraryPath = Path.Combine(moduleDirectory, libraryName);
                if (File.Exists(libraryPath)) NativeLibrary.Load(libraryPath);
            }
        }
        _isLoaded = true;
    }

    // ModuleDirectory — この DLL のファイルがあるフォルダ。分からなければ null
    //   NativeAOT では Assembly.Location が空なので、この DLL の中の関数のアドレスから OS にモジュールを尋ねる。
    private static string? ModuleDirectory()
    {
        delegate* unmanaged<void> addressInThisModule = &AddressMarker;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (nint)addressInThisModule, out nint moduleHandle))
            return null;
        char* pathBuffer = stackalloc char[MaxModulePathLength];
        uint pathLength = GetModuleFileNameW(moduleHandle, pathBuffer, MaxModulePathLength);
        if (pathLength == 0 || pathLength >= MaxModulePathLength) return null;
        return Path.GetDirectoryName(new string(pathBuffer, 0, (int)pathLength));
    }

    // AddressMarker — アドレスを取るためだけの関数（呼ばない）
    [UnmanagedCallersOnly]
    private static void AddressMarker() { }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetModuleHandleExW(uint flags, nint address, out nint moduleHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetModuleFileNameW(nint moduleHandle, char* fileName, uint size);
}

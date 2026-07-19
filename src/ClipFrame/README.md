# ClipFrame (画面部分切り取り君II) — 実装

`ClipFrame_仕様書.md`(版 0.1)に基づく実装です。画面の任意領域をリアルタイムに映す通常ウィンドウ(ミラーウィンドウ)を提供し、Teams 等のウィンドウ共有で実質的な領域共有を実現します。

## ビルド / 実行

```powershell
cd src/ClipFrame

# 開発ビルド + 実行
dotnet run -c Debug

# 配布用の単一 EXE(self-contained, ランタイム同梱 — 仕様 §7)
dotnet publish -c Release
# 出力: bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/ClipFrame.exe
```

要件: .NET 10 SDK、Windows 10 2004 (build 19041) 以降。

## 構成

```
src/ClipFrame/
├─ App.xaml(.cs)              起動・全体配線(RegionManager と2ウィンドウ、キャプチャ)
├─ app.manifest               PerMonitorV2 DPI、asInvoker
├─ Core/
│  ├─ RegionManager.cs        共有領域(物理ピクセル)の単一の真実。変更/確定イベントを発行
│  └─ PresetStore.cs          領域プリセットの JSON 永続化(%APPDATA%\ClipFrame\presets.json)
├─ Capture/CaptureEngine.cs   WGC でモニタ全体をキャプチャ→領域をGPUクロップ→CPU読み出し
├─ Native/
│  ├─ NativeMethods.cs        Win32 相互運用(affinity, region, hit-test, DPI, monitor …)
│  └─ Direct3D11Interop.cs    Vortice D3D11 ⇄ WinRT(Windows.Graphics.Capture)の橋渡し
└─ UI/
   ├─ FrameOverlayWindow      枠オーバーレイ(領域刳り抜き・タグ・リサイズ/移動・除外・プリセット)
   ├─ MirrorWindow            ミラーウィンドウ(WriteableBitmap 表示・最小化退避・自動配置)
   └─ InputDialog.cs          プリセット名入力用の簡易モーダル

Assets/appicon.ico          アプリ/ミラーウィンドウ用アイコン
tools/make-icon.ps1         アイコン再生成スクリプト(System.Drawing で描画→PNG-in-ICO)
```

## 仕様との対応

| 仕様 | 実装状況 |
|---|---|
| §4.1 中央刳り抜き形状(`SetWindowRgn`)・素通し | ✅ 枠リング=ウィンドウ背景、内側は物理的に刳り抜き click-through |
| §4.1 枠線は領域の外側 | ✅ ウィンドウを枠幅ぶん外側に拡張、穴=共有領域(WYSIWYG) |
| §4.1 `WDA_EXCLUDEFROMCAPTURE` | ✅ 枠オーバーレイ全体を除外 |
| §4.2 `WM_NCHITTEST` リサイズ(辺/角)・6px当たり判定 | ✅ 角は拡大グラブ |
| §4.2 解像度バッジのリアルタイム表示 | ✅ タグ内に WxH を即時更新 |
| §4.2 Shift でアスペクト比固定 | ✅ `WM_SIZING` で拘束 |
| §4.2/§9 解像度スナップ | ✅ 1280×720 等へ吸着(既定ON・メニュー切替・Altで一時無効) |
| §4.2 最小サイズ | ✅ `WM_GETMINMAXINFO`(160×120 DIP) |
| §4.3 タグで領域移動(`HTCAPTION`) | ✅ |
| §4.3 グリップでタグ横スライド | ✅ |
| §4.4 コンテキストメニュー | ✅ 一時停止/再開・プリセット保存/呼び出し/削除・終了が動作 |
| §4.4 領域プリセットの保存/呼び出し | ✅ `%APPDATA%\ClipFrame\presets.json` に永続化(名前付き・上書き・削除) |
| §4.5 ドラッグ/リサイズ中は映像を追従させない | ✅ 変更開始で freeze、確定(mouse up)で更新 |
| §5 通常ウィンドウ・タイトル "ClipFrame View"・Alt+Tab | ✅ |
| §5 ミラーは除外しない | ✅ 除外は枠のみ |
| §5 領域の外へ自動配置・自己映り込み警告 | ✅ |
| §5 `SC_MINIMIZE` フック→画面隅退避 | ✅ |
| §5 アスペクト比追従 | ✅ 確定時にミラー高さを追従 |
| §6 WGC `CreateForMonitor`+クロップ | ✅ B8G8R8A8、staging texture 経由で読み出し |
| §6 フレームレート制御 | ✅ 既定 15fps にスロットル(メニューで 10/15/20/30 切替可) |
| §5 ミラーウィンドウ専用アイコン | ✅ 「切り取る枠」モチーフの専用アイコン(EXE・ウィンドウ共通) |
| §7 C#/WPF・.NET 10・単一EXE | ✅ Vortice.Windows で D3D11 相互運用 |

## 未実装 / 割り切り(仕様 §9 の未決事項に対応)

- **ホットキー**: 未実装(不要と判断)。
- **複数モニタをまたぐ領域**: 領域中心のモニタを対象にクロップ(初版は単一モニタ内想定)。
- **枠/タグの寸法・配色**: 暫定値。枠幅6px・タグ150×26px 等はチューニング前提。

## 動作検証

環境変数を設定して起動すると、対応する処理を実行後に終了するヘッドレス自己診断が走ります。

| 環境変数 | 内容 | 検証結果 |
|---|---|---|
| `CLIPFRAME_DIAG=<path>` | キャプチャのフレーム生成を確認 | `framesProduced=3 capturing=True` |
| `CLIPFRAME_DIAG_PRESET=<path>` | プリセットの保存→再読込→一致 | `reloaded=True rectMatch=True` |
| `CLIPFRAME_DIAG_FPS=<path>` | 領域内を強制描画更新し fps を計測 | 目標60→実測35.6 / 目標15→実測13.2(スロットル有効) |

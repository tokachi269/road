# 道路アイコンの制作

Python + PillowでPNGを生成する。CS1、Runtime DLL、Blenderは不要。
Runtimeは見た目確認用のconsumerで、アイコンを描画しない。

## 最小限の設定

共通設定は[icons.json](icons.json)。寸法は128×128画像内のpx単位。

| 設定 | 既定値 | 用途 |
| --- | --- | --- |
| `footer_height` | 20 | 下部帯の高さ。変更すると図の領域も追従 |
| `font_size` | 16 | ラベル文字の大きさ |
| `font` | `NotoSans-Regular.ttf` | 設定ファイルからの相対path |
| `overlay` | `null` | 共通透過PNGの相対path。不要ならnull |
| `import_job_names` | `{}` | road IDとimport job名が違う場合だけ対応を指定 |

例: `"import_job_names": {"jp-1x2l": "My Road"}`なら
`My Road_thumb.png`など5状態を出力する。省略時はroad IDがjob名になる。
fontに収まらないfooter、重複job名、不正画像は出力前にエラーとする。

## 生成とインポート

```powershell
python tools/generate_road_icons.py
```

- `build/road-icons/import/`: RoadImporter用。`<job>_thumb.png`と
  `_hovered.png / _pressed.png / _focused.png / _disabled.png`。5状態は同じ絵。
- `build/road-icons/contact-sheet.png`: 縮小一覧。
- `build/road-icons/overlay-template.png`: 完全透過の作業用。既存fileは上書きしない。

import用PNGは同じjobの`<job>_data.xml`等と一緒にRoadImporterの`import` directoryへ置く。
既存の`Environment.PrepareJob`がImport directoryへ移し、`ApplyThumbnail`がasset保存時に読む。
PNG生成はFBX/XMLの作成やCRP保存を実行しない。

## Runtimeでも同じPNGを確認する

```powershell
.\scripts\generate-road-icons.ps1
.\scripts\publish-runtime-hot.ps1
```

PowerShell wrapperはPythonを実行し、通常の制作出力に加えて
`build/runtime-preview/icons`へ同じPNGを置く。`-PreviewPath`で別previewを指定できる。
hot publishとinitial stagingもoffline生成を呼ぶ。preview.pathは生成wrapperでは変えない。
PNG生成だけでは起動中のCS1が画像を再読込みした扱いにはしない。

## 共通の透過PNG

templateをコピーし、128×128の透過PNGを制作する。半透明も使用できる。
`overlay`へ`"overlay.png"`と指定すれば、設定ファイルと同じdirectoryの画像を全道路へ合成する。
一時指定は`--overlay`、PowerShellでは`-OverlayPath`。制作画像を生成toolで上書きしない。
publish時は共通設定で再生成するため、継続して使う画像は`overlay`へ指定する。
画像は128×128 PNG・1 MiB以下。図やラベルを隠さない配置は画像側で調整する。

## Font

同梱のNoto Sans RegularはSIL OFL 1.1。出典とlicenseは[OFL-NotoSans.txt](OFL-NotoSans.txt)。
元font: https://github.com/notofonts/noto-fonts/blob/main/hinted/ttf/NotoSans/NotoSans-Regular.ttf
数字・plusはTrueTypeを4倍で描画して縮小する。U+2192を持たないため右矢印だけstroke描画する。
fontやglyph maskをRuntimeへ埋め込まない。

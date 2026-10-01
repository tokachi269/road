# road

Cities: Skylines 1向けの道路ジェネレータです。
Blenderで道路を編集し、RoadImporterまたはRuntimeHostへ出力します。

CSURは参考実装として利用しますが、生成物をCSURへ依存させません。

## 文書

作業を始める前に[文書案内](docs/README.md)を参照してください。
文書は作業目的ごとに分かれています。

## 主なディレクトリ

| パス | 内容 |
| --- | --- |
| `blender_addon/road_builder` | Blender add-on |
| `specs` | 道路定義 |
| `textures` | atlas定義と制作物 |
| `catalog` | Runtime向けTSV |
| `src/RoadImporter` | RoadImporterのローカル版 |
| `src/RoadRuntimeHost.*` | 通常マップ用LoaderとRuntime |
| `tests` | contract testとsmoke test |
| `scripts` | build、検証、配置 |

## 初回確認

`config/toolchain.example.psd1`を`config/toolchain.psd1`へコピーします。
ローカルのツールパスは、この追跡外ファイルへ設定してください。

```powershell
.\scripts\check-environment.ps1
.\scripts\run-blender.ps1 -Script .\generator\smoke.py
.\scripts\build-road-importer.ps1
```

## Blender add-on

```powershell
.\scripts\install-blender-addon.ps1
```

Blenderの3D Viewで`N`キーを押し、`Road`タブを開きます。
操作方法は[Blenderでの作業](docs/blender-workflow.md)を参照してください。

## Runtime preview

```powershell
$previewPath = Join-Path $PWD 'build\runtime-preview'
.\scripts\stage-runtime-host.ps1 -PreviewPath $previewPath
```

通常マップへの配置と制約は[Runtime preview](docs/runtime-preview.md)に記載しています。

## 検証

```powershell
python tools/arch_lint.py
python -m unittest discover -s tests -p '*_test.py'
```

変更内容ごとの追加検証は[検証方法](docs/testing.md)を参照してください。

Blender previewとRuntime previewの成功だけでは、完成道路とは扱いません。
LOD、RoadImporter出力、Asset Editor、実ゲームでの確認が必要です。

# 検証方法

この文書は、変更に応じて実行する検証を示します。
各テストが守る契約は[契約と検証の対応](contract-map.md)を参照してください。

## 通常の変更

```powershell
python tools/arch_lint.py
python -m unittest discover -s tests -p '*_test.py'
```

architecture lintは、依存方向、owner、文書構成を確認します。
behaviorの正しさはunit testで確認します。

## Blenderを変更した場合

```powershell
.\scripts\run-blender.ps1 -Script .\tests\blender_addon_smoke.py
```

smokeはJSON、全mode、mesh、UV、material、Runtime bundleを確認します。
形状を目で確認するときは、補助renderを生成できます。

```powershell
.\scripts\run-blender.ps1 -Script .\tests\render_geometry_preview.py
```

render画像だけで寸法やtopologyの正しさを判定しません。

## RoadImporterを変更した場合

```powershell
.\scripts\build-road-importer.ps1
```

build成功は、Asset Editorでのimport成功を保証しません。

## Runtimeを変更した場合

```powershell
$previewPath = Join-Path $PWD 'build\smoke\runtime-preview'
.\scripts\stage-runtime-host.ps1 -PreviewPath $previewPath
.\scripts\test-runtime-contract.ps1 -PreviewPath $previewPath
```

このcontractは、JSON reader、catalog、bundle、診断ログを確認します。
Prefab登録、shader表示、map再読込は実ゲームで確認します。

## 人が確認する項目

次の項目は自動テストだけでは判定できません。

- Blender上の見た目と編集しやすさ
- Asset Editorへのimport
- shader、AO、normalの見え方
- カーブ、交差点、transitionの接続
- IMT、TM:PE、Move Itとの操作競合
- save後に道路が正しく復元されるか

実ゲームを起動していない場合は、これらを完了扱いにしません。

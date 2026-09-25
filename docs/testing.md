# 検証方針

Test数ではなく、道路定義の正本、導出規則、Blender生成、CS1 importの各fault classを別のproofで確認する。

## Primary proof

`tests/domain_contract_test.py`と`tests/geometry_plan_test.py`はBlenderなしで次を検証する。

- 2・3・4車線の幅がlane入力から導出される。
- Boundary identityがstable lane IDと隣接関係から導出される。
- Lane追加で無関係なedge boundary IDが変わらない。
- Segment/node長とslice数のownerがdomainに一つだけある。
- 主桁本数が偶数で、桁間隔がJIS標準の2.6/3.2/3.8mから選ばれ、床版幅を変更しても桁外形が床版からはみ出さない。
- 主桁中心が左右対称でdeck内に収まる。

実行:

```powershell
python -m unittest tests.domain_contract_test tests.geometry_plan_test
```

## Structural proof

`tools/arch_manifest.json`はsourceをlayerへexactly onceで分類し、domainへの`bpy`依存等を禁止する。`tools/arch_lint.py`は文書contractと主要定数の単一ownerも検査する。

```powershell
python tools/arch_lint.py
python -m unittest tests.architecture_harness_test
```

Architecture lintはbehaviorを証明しない。Token guardをgeometry correctnessの代用にしない。

## Representative end-to-end

Blender add-onの代表経路は、JSON import、全5 mode生成、UV範囲、64 m、slice、node中央分割、marking切替、JSON round-tripをbackground Blenderで通す。

ElevatedのGeometry Planは、既定4車線preview（床版幅21m）で3.8m間隔・6本・桁高2.5mの主桁、structure material、床版下面より低い主桁下端が生成されることも数値検査する。形状確認用renderは補助確認として次で作る。

カスタムElevated端部の代表経路では、右側基準Mesh 1つを左右へ反転配置し、連続側面がsegment 20/node 8 sliceへ分割されること、`CS1_NO_SPLIT`の柵面が未分割であること、床版厚変更でZ<0だけが追従すること、従来fasciaが消えること、最下辺と床版下面が同じ頂点を共有することをbackground Blenderで検査する。

```powershell
& 'G:\Program Files\Blender\stable\blender-5.1.0-windows-x64\blender-5.1.0-windows-x64\blender.exe' `
  --background 'D:\GitHub\road\build\smoke\road-builder-addon-smoke.blend' `
  --python 'D:\GitHub\road\tests\render_geometry_preview.py'
```

```powershell
& 'G:\Program Files\Blender\stable\blender-5.1.0-windows-x64\blender-5.1.0-windows-x64\blender.exe' `
  --background --factory-startup `
  --python 'D:\GitHub\road\tests\blender_addon_smoke.py'
```

RoadImporter側を変更した場合は次も実行する。

```powershell
Set-Location D:\GitHub\road
.\scripts\build-road-importer.ps1
```

Runtime previewを変更した場合は、catalog、Blender bundle、.NET 3.5 reader、CS1参照build、versioned stagingを別々に確認する。

```powershell
python -m unittest tests.runtime_catalog_test
& 'G:\Program Files\Blender\stable\blender-5.1.0-windows-x64\blender-5.1.0-windows-x64\blender.exe' `
  --background --factory-startup `
  --python 'D:\GitHub\road\tests\blender_addon_smoke.py'
.\scripts\stage-runtime-host.ps1 -PreviewPath 'D:\GitHub\road\build\smoke\runtime-preview'
.\scripts\test-runtime-contract.ps1 -PreviewPath 'D:\GitHub\road\build\smoke\runtime-preview'
```

`RUNTIME_CONTRACT_OK`はBlenderが出したJSONをRuntime DLLと同じ.NET 3.5 serializerで読めることに加え、専用JSON Linesログへ成功事象と意図的なJSON型不一致がそれぞれ`SUCCESS` / `CONTRACT_TYPE`で記録されることを証明する。`runtime.current`による差替え、Prefab登録、shader描画、map save再読込、Adaptive Roads条件、接続形状は実ゲーム起動なしでは証明しない。

これらはAsset Editor内の見た目、shader、AO、selector、カーブ接続を証明しない。最終gateには実ゲームimportが必要である。

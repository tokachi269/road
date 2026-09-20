# road

Cities: Skylines 1向けの独自道路ジェネレータ開発環境です。CSURの道路規格には依存せず、RoadImporterの入出力契約だけを利用します。

## 構成

- `generator/`: Blender 5.1用Pythonコード
- `blender_addon/road_builder/`: Blender内で道路を編集・プレビューするアドオン
- `specs/`: 独自道路定義
- `src/RoadImporter/`: RoadImporterのローカルビルド用コピー。テクスチャをCRPへ格納する設定
- `references/RoadImporter/`: upstream参照用submodule
- `references/CSUR/`: XMLと全モード実装の参照用submodule。生成処理には使わない
- `scripts/`: 環境確認、Blender起動、Importerビルド・配置、生成物配置
- `docs/cs1-road-requirements.md`: importer・サンプル・CSURを照合した実装基準

## 確認済みツール

- Blender 5.1.0: `G:\Program Files\Blender\stable\blender-5.1.0-windows-x64\blender-5.1.0-windows-x64\blender.exe`
- Cities: Skylines 1: `C:\Program Files (x86)\SteamLibrary\steamapps\common\Cities_Skylines`
- Visual Studio MSBuild: `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`

## 初回確認

```powershell
Set-Location D:\GitHub\road
.\scripts\check-environment.ps1
.\scripts\run-blender.ps1 -Script .\generator\smoke.py
.\scripts\build-road-importer.ps1
```

Blenderのテスト結果は`build/smoke/`へ出力されます。RoadImporterのビルド結果は`src/RoadImporter/bin/Release/RoadImporter.dll`です。

ゲームへDLLを配置するときだけ次を実行します。

```powershell
.\scripts\install-road-importer.ps1
```

## Blender内で編集する

開発用アドオンをBlender 5.1へリンクして有効化します。

```powershell
.\scripts\install-blender-addon.ps1
```

Blenderを通常起動し、3D Viewで`N`キーを押し、`Road`タブを開きます。以下をUIから編集できます。

- 道路名と、左から右へ並ぶlane一覧
- laneごとの領域、幅、方向、種別、車種、速度、縦/停止offset、接続可否
- 歩道幅、curb高さ、左右路肩幅
- Strip間boundaryの一覧と、boundaryごとの線ON/OFF・意味・style
- 共有marking styleの線幅、asphalt余白を含むmarking領域幅
- Ground / Elevated / Bridge / Tunnel Entrance / Tunnel
- 高架・橋梁の高さと床版厚
- トンネル深さと建築限界
- JSONの読み込み・保存
- 64 m固定のnode、道路中央`X=0`での左右メッシュ分割
- 地面同高路面とcurb高さ分だけ低い路面、およびnode内の接続スロープ

`Build active mode`または`Build all modes`で専用コレクションへ生成します。生成物は通常のMeshなので、生成後はEdit Mode、Modifier、Materialで直接編集できます。同じモードを再生成すると、そのモードの専用コレクション内だけが置き換わります。

各モードは`segment`と`node`の2オブジェクトだけを生成します。歩道・路肩・curbはそれぞれ別プリミティブにせず、各オブジェクト内の道路表面として生成します。Groundの下面・端面・外側面など、通常見えない面は作りません。nodeの中央分割は1オブジェクト内の独立した左右面として保持します。

segmentとnodeはどちらも設定変更不可の64 m固定です。Groundのtransitionはnodeの64 m全体を使い、segment側の路面高から接続先の路面高まで傾斜させます。
カーブ変形用にsegmentは長手方向20分割（3.2 m間隔）、nodeは8分割（8 m間隔）です。分割は全断面と全モードへ適用されます。

JSON schema v3では物理断面のstrip、CS1 lane、strip間boundary、共有marking styleを別の正本として保存します。線はlaneではなくboundaryに所属し、laneとboundaryはstable IDで参照します。Lane中心位置、道路総幅、marking位置、mesh、UVは保存値から再生成します。

現段階はBlender内の形状・道路定義previewです。UVは実装済みですが、最終texture atlas、LOD FBX、segment/nodeのflag selector、全NetInfo/AI XMLを生成してゲーム内検証するまでは「インポート可能な完成道路」とは扱いません。根拠と未実装項目は`docs/cs1-road-requirements.md`に集約しています。

独自ジェネレータが次の構造を出力した後は、`stage-import.ps1`でゲーム側へ配置できます。

```text
build/<road-name>/
├─ imports.txt
├─ import/
│  ├─ <road-name>_data.xml
│  ├─ <road-name>_*.FBX
│  └─ <road-name>_*.png
└─ textures/
   └─ *.png
```

```powershell
.\scripts\stage-import.ps1 -BuildDirectory .\build\my-road
```

`stage-import.ps1`は既存のRoadImporterディレクトリ全体を消去しません。同名ファイルだけを更新します。

## RoadImporterのローカル変更

upstreamのプロジェクトは`IAssetInfo.cs`をコンパイル対象へ含めておらず、そのままでは現在の環境でビルドできません。`src/RoadImporter`ではこれを追加しています。

また、独自道路をCSUR Loaderへ依存させないため、`Environment.optLevel`を`0`にしています。これによりテクスチャは各CRPへ格納されます。多数の道路で同一テクスチャを共有する場合はファイルサイズが増えます。

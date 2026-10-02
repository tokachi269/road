# road

Cities: Skylines 1向けの独自道路ジェネレータ開発環境です。CSURの道路規格には依存せず、RoadImporterの入出力契約だけを利用します。

## 構成

- `generator/`: Blender 5.1用Pythonコード
- `blender_addon/road_builder/`: Blender内で道路を編集・プレビューするアドオン
- `specs/`: 独自道路定義
- `src/RoadImporter/`: RoadImporterのローカルビルド用コピー。テクスチャをCRPへ格納する設定
- `src/RoadRuntimeHost.*`: 通常マップ上の差分preview用Loaderとhot-reload Runtime
- `catalog/`: 道路、lane、Prop、条件、試験区画のTSV正本
- `references/RoadImporter/`: upstream参照用submodule
- `references/CSUR/`: XMLと全モード実装の参照用submodule。生成処理には使わない
- `scripts/`: 環境確認、Blender起動、Importerビルド・配置、生成物配置
- `docs/README.md`: 文書の入口、正本の優先順位、文書一覧
- `docs/cs1-road-requirements.md`: importer・サンプル・CSURを照合した仕様資料
- `docs/engineering/agent_harness.md`: agent支援作業のscope・証拠・停止条件

## 確認済みツール

- Blender 5.1.0
- Cities: Skylines 1
- Visual Studio MSBuild

ローカルの絶対パスは追跡しない。`config/toolchain.example.psd1`を
`config/toolchain.psd1`へコピーし、各自の環境だけで設定する。

## 初回確認

```powershell
Set-Location <repository-root>
.\scripts\check-environment.ps1
.\scripts\run-blender.ps1 -Script .\generator\smoke.py
.\scripts\build-road-importer.ps1
```

Blenderのテスト結果は`build/smoke/`へ出力されます。RoadImporterのビルド結果は`src/RoadImporter/bin/Release/RoadImporter.dll`です。

## 通常マップで差分previewする

Runtime側で断面meshは生成しません。Blenderで生成・編集した完成meshをbundleへ書き出し、通常マップ用Hostが同名Prefabへ反映します。

```powershell
.\scripts\run-blender.ps1 -Script .\generator\export_vehicle_variants.py -ScriptArguments @('--output', '.\build\vehicle-variants-preview')
.\scripts\stage-runtime-host.ps1 -PreviewPath (Join-Path $PWD 'build\vehicle-variants-preview')
.\scripts\publish-runtime-hot.ps1 -PreviewPath (Join-Path $PWD 'build\vehicle-variants-preview')
```

初回だけ`install-runtime-host.ps1`でLoaderを配置します。以後はBlenderの`Runtime preview`で同じdirectoryへ出力し、必要なときだけstagingしたversioned Runtime DLLをMods directoryへ上書きします。詳細、制約、未検証項目は[docs/runtime-preview.md](docs/runtime-preview.md)と[docs/runtime-workboard.md](docs/runtime-workboard.md)に記載しています。

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
- laneごとの領域、幅、方向、種別、車種と、速度・停止offset・接続可否のsource/override
- 歩道幅、curb高さ、左右路肩幅
- 道路単位の路側線、lane間線、中央線規則
- 共有marking styleの線幅、asphalt余白を含むmarking領域幅
- Ground / Elevated / Bridge / Tunnel Entrance / Tunnel
- 高架・橋梁の高さと床版厚
- Elevated左右端へ反転配置するカスタム側面・手すりMesh
- トンネル深さと建築限界
- JSONの読み込み・保存
- 片方向0〜4、合計最大8の標準vehicle-lane道路14種類の一括追加
- 64 m固定のnode、道路中央`X=0`での左右メッシュ分割
- 地面同高路面とcurb高さ分だけ低い路面、およびnode内の接続スロープ

`Build active mode`または`Build all modes`で専用コレクションへ生成します。生成物は通常のMeshなので、生成後はEdit Mode、Modifier、Materialで直接編集できます。同じモードを再生成すると、そのモードの専用コレクション内だけが置き換わります。

アドオンのPythonを変更した場合、Blenderの再起動は不要です。Roadサイドバー下部の`Development > Reload Scripts`ボタンで`domain.py`と`geometry_plan.py`を含めて再読込できます。コード反映後の形状は自動更新されないため、`Build active mode`または`Build all modes`で再生成します。

### Elevatedのカスタム端部Mesh

Elevatedモードでは`Edge mesh (right basis)`へ1つのMesh Objectを指定します。入力を右端へそのまま配置し、左端はX反転して配置します。

入力Objectは次の規約で作成します。

- 右側用として作り、Mesh Objectの原点を、路面上面と右外端が交わる横断面上の点（local X=0、Z=0）に置く。床版下端は原点から-X側（道路内側）へ作る。左側では自動的にX反転される。
- 長手方向をY軸とし、カーブ追従させる連続面をlocal Y=-32～+32mまで作る。
- 路面より上の手すり等はZ>0、床版側面はZ<0へ作る。負のZだけが`Elevated deck depth`に合わせて伸縮する。
- 最下辺は64mを通る1本の長手辺にする。左は原点から+X側、右は-X側へ入るように作る。垂直側面ならX=0でよい。
- 長手分割したくない柵等の頂点を`CS1_NO_SPLIT` vertex groupへ入れる。その頂点に触れる面は分割しない。グループ名はUIで変更でき、グループがなければ全ての面を分割する。
- Objectのlocationは配置に使わない。rotationとscaleは形状へ適用される。

生成時は連続側面をsegmentで20分割、nodeで8分割し、最下辺を床版下面へ溶接します。左側は面の頂点順も反転して法線を維持します。カスタム端部指定中は従来の垂直fasciaを生成せず、道路表面・床版・主桁・左右端部を同じ生成Mesh Objectへまとめます。入力Objectへの参照は`.blend`内の編集状態であり、現行JSON schema v4には保存しません。

各モードは`segment`と`node`の2オブジェクトだけを生成します。歩道・路肩・curbはそれぞれ別プリミティブにせず、各オブジェクト内の道路表面として生成します。Groundの下面・端面・外側面など、通常見えない面は作りません。nodeの中央分割は1オブジェクト内の独立した左右面として保持します。

segmentとnodeはどちらも設定変更不可の64 m固定です。Groundのtransitionはnodeの64 m全体を使い、segment側の路面高から接続先の路面高まで傾斜させます。
カーブ変形用にsegmentは長手方向20分割（3.2 m間隔）、nodeは8分割（8 m間隔）です。分割は全断面と全モードへ適用されます。

JSON schema v4では物理断面のstrip、CS1 lane、strip間boundary、共有marking styleを保存します。laneの速度、停止offset、接続可否はglobal既定値、1段profile、lane明示overrideの順で解決し、profile本体は`profiles.json`へ分離します。線はlaneではなくboundaryに所属し、laneとboundaryはstable IDで参照します。Lane中心位置、道路総幅、marking位置、mesh、UVは保存値から再生成します。

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

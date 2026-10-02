# 通常マップRuntime preview

> 文書種別: 運用手順

## 目的

Asset Editorを往復せず、Blenderで作った道路・Prop・Decal meshと表形式metadataを通常マップへ反映する開発用Hostである。最終CRPを作る機能ではない。

Runtimeで断面からmeshを生成しない。道路meshの正本はBlenderの生成後meshであり、手編集した頂点、法線、UVもそのままbundleへ入る。Runtimeが自動生成するgeometryはない。接続試験では、そのmeshを使うnetwork node/segmentの配置だけを生成する。

道路meshの各material bundleは`main_texture_scale = [1, 0.5]`を持つ。RuntimeHostはこれをUnity Materialの`mainTextureScale`へ設定する。長手周期の変更はmaterial scaleまたは既存faceのV範囲で行い、周期だけのためにmesh entryを追加しない。

Textureは`textures/road.psd`を直接読むのではなく、Photoshop Generator Plugins / Image AssetsのPNG出力を使う。各familyのDiffuse groupは次の名前にし、Generatorを有効にする。

```text
surface_d.png
structure_d.png
tunnel_d.png
```

PhotoshopはPSD横の`road-assets` directoryへ出力する。Diffuseの`d`だけが必須で、各familyに任意で`_a / _p / _r / _n / _s`を追加できる。例は`surface_a.png`、`structure_n.png`、`tunnel_s.png`である。PSDは画像制作物であり、配置座標の正本ではない。出力契約とUV座標は`textures/dimensions.json`が所有する。Runtime exportは存在する画像が2048x2048 PNGか検証し、preview directoryの`textures`へatomic copyする。

CS1 Runtime materialでは6枚を個別propertyへ渡さない。`d`は`_MainTex`、`a / p / r`はchannel packedした`_APRMap`、`n / s`はchannel packedした`_XYSMap`になる。任意画像がないchannelにはCS1既定値を入れる。Blenderでは`d / a / n / s`を表示へ接続する。`p / r`はCS1 theme textureとのblend maskなので、theme入力がないBlender上では画像nodeの読込までとし、推測した色への置換はしない。

`packed_textures`を追加したpreview manifest / road bundle / prop bundleはschema version 2とする。現行Runtimeは移行用にversion 1も読み込めるが、version 2を出力することで旧Runtimeが未知のtexture fieldを黙って無視するのを防ぐ。
各groupは透明部分を含めてcanvas全体が2048x2048になるようにする。Generatorがlayer boundsで切り詰めたPNGはexport時に拒否され、意図せずUV scaleを変えない。Blenderは読み込み済みの3画像を約1秒ごとに監視し、変更時だけviewport画像を再読込する。Developmentの`Reload Photoshop Generator PNGs`でも手動再読込できる。

## ファイル

`catalog/*.tsv`はExcel、LibreOffice、表計算ソフトで編集できる。列名は固定し、行順だけに意味を持たせない。

| 表 | 所有するもの |
|---|---|
| `roads.tsv` | stable road ID、実行時Prefab名、clone元、UI分類、variant順、試験scenario |
| `lanes.tsv` | road内stable lane ID、順序、幅、位置、CS1 lane metadata |
| `props.tsv` | stable Prop ID、ローカルPrefab名、clone元、Prop/Decal種別、shader、mesh bundle |
| `prop_placements.tsv` | road/lane上のProp参照、位置、角度、repeat、確率、条件 |
| `conditions.tsv` | namespaced required/forbidden flag。Adaptive Roads用値も文字列として保持できる |
| `geometry_bindings.tsv` | mode/kind/materialとselector条件、direct-connectの対応 |
| `test_scenarios.tsv` | 自動試験区画の種類、原点、道路間隔 |

`m_UIPriority`は`category → family_order → family → variant_order → road_id`のsortからcompile時に自動採番する。一件ごと手で直さない。

通常マップで使うlane metadataはcatalog側を適用する。Blender bundleにもmesh生成時のlane snapshotを入れ、同じ`road_id`がcatalogにある場合は、適用前に順序、ID、位置、幅、vertical/stop offset、速度、方向、lane/vehicle種別、接続可否を比較する。数値はJSONのfloat丸めを許容する小さい誤差内だけ同一とみなす。不一致時はcatalogで上書きせず道路全体を未適用にし、`lane_contract_mismatch`として差分を専用ログへ出す。Runtimeがmesh幅やlane表を推測補正することはしない。

## Blender export

Road sidebarの`Runtime preview`で次を設定する。

1. `Runtime road ID`を`roads.tsv`の`road_id`と一致させる。
2. `Runtime prefab name`と`Template prefab`を設定する。
3. Scene共通の`Runtime output`をHostの`preview.path`と同じdirectoryへ向ける。
4. `Build and export Runtime bundle`でScene内の全道路・全modeを一括出力する。
   manifestはその時点のScene内道路だけに確定する。profileを共有する道路のbundleを
   一部だけ古い状態にしないため、通常の確認ではこの一括出力を使う。

道路単位の`Export active road`と`Auto export every second`は局所デバッグ用として残す。
これらは対象道路だけを更新するため、複数道路の正規出力には使わない。

標準14 variantをまとめてゲーム確認用directoryへ出す場合は次を実行する。

```powershell
.\scripts\run-blender.ps1 -Script .\generator\export_vehicle_variants.py -ScriptArguments @('--output', '.\build\vehicle-variants-preview')
.\scripts\stage-runtime-host.ps1 -PreviewPath (Join-Path $PWD 'build\vehicle-variants-preview')
# ゲーム停止中
.\scripts\install-runtime-host.ps1 -PreviewPath (Join-Path $PWD 'build\vehicle-variants-preview')
# ゲーム起動中
.\scripts\publish-runtime-hot.ps1 -PreviewPath (Join-Path $PWD 'build\vehicle-variants-preview')
```

1コマンド目はBlender Sceneへ14道路を構築し、全modeのbundleと確認用authoring JSONを
出力する。2コマンド目は同じdirectoryへcatalogをcompileする。ゲーム停止中はinstall、
起動中はhot publishでRuntime Hostの`preview.path`をそのdirectoryへ向ける。ゲーム内の見た目と接続はこの処理だけでは
確認済みにならない。

一括exportは各road bundleとtextureを書き終えてから、Scene内の道路集合でmanifestをatomic replaceする。Hostは途中の出力を正規更新として読まない。個別auto exportは約1秒ごとに選択中道路の入力差分を比較し、変更時だけ対象bundleを更新する。

Prop/Decalは選択Meshを`Export selected Prop/Decal mesh`で出す。使用materialは現状1つだけに制限している。catalogの`mesh_bundle`へ`props/<prop-id>.json`を指定する。`textures`にはshader property名とpreview directory相対画像path、`material_properties`には`_DecalSize`等のfloatまたは2/4要素vectorをJSONで指定できる。値はRuntimeがmeshから推測しない。新規Prop登録には実在する`template_name`が必要で、未指定時にRuntimeが推測して適当なvanilla Propを選ぶことはしない。

座標はBlenderの`X=横、Y=道路長手、Z=上`からUnityの`X=横、Y=上、Z=道路長手`へexport時に変換する。preview配置用Object translationはmeshへ焼かない。

## buildとstaging

```powershell
Set-Location <repository-root>
.\scripts\stage-runtime-host.ps1 -PreviewPath (Join-Path $PWD 'build\runtime-preview')
```

出力は`build/runtime-host`である。

```text
RoadRuntimeHost.Loader.dll
preview.path
runtime.current
runtime/
  RoadRuntimeHost.Runtime.<content-hash>.dll
```

初回だけ`install-runtime-host.ps1`でCities: SkylinesのMods directoryへ配置し、ゲームのContent Managerで有効化する。2026-09-25時点でローカルMods directoryへの初回配置までは実行済みだが、ユーザー不在のためContent Managerでの有効化とゲーム起動は実行していない。

ゲーム起動後にC# Runtime実装を変更した場合は、Loaderを上書きせず次だけを実行する。

```powershell
.\scripts\publish-runtime-hot.ps1
```

hash名の新DLLを先にコピーし、最後に`runtime.current`をatomicに置換する。RuntimeはbuildごとにAssemblyVersionも変える。ファイル名だけ変えてAssembly identityが同じだとCS1のMonoが既に読み込んだ旧assemblyを返すためである。切替後は、ModTools等によるplugin型走査へ古い依存関係を露出させないため、現在版以外のRuntime DLLを削除する。既にMonoへ読み込まれたassemblyはプロセス終了まで残る。Loader自体を変更した場合だけゲームを終了して再installする。

## ロード済みPrefabの限定取得

RuntimeHostが動作中なら、ロード済み`NetInfo`から必要な区分だけを取得できる。全Prefabを1つのJSONへdumpする機能はない。検索・詳細は最大20件、requestは16 KiB、responseは64 KiBに制限される。

```powershell
.\scripts\query-runtime-prefab.ps1 -Command find_net -Filter 'Basic Road'
.\scripts\query-runtime-prefab.ps1 -Command inspect_net -PrefabName 'Basic Road' -Section summary
.\scripts\query-runtime-prefab.ps1 -Command inspect_net -PrefabName 'Basic Road' -Section lanes
.\scripts\query-runtime-prefab.ps1 -Command inspect_net -PrefabName 'Basic Road' -Section lane_props -LaneIndex 2
.\scripts\query-runtime-prefab.ps1 -Command inspect_net -PrefabName 'Basic Road' -Section nodes
```

`find_net`は名前とlane数だけを返す。`inspect_net`は完全一致のPrefab名を要求し、`summary / lanes / lane_props / segments / nodes`から1区分だけ返す。20件を超える場合は`truncated=true`となるため、`-Offset`で次ページを取得する。応答はpreview directoryの`inspect.response.json`へ置かれるが、道路定義の正本として保存・再利用しない。

## 専用診断ログ

Hostの詳細ログはUnityの共通`output_log.txt`ではなく、MOD directory内の次のファイルへJSON Lines形式で出す。

```text
RoadRuntimeHost/logs/RoadRuntimeHost.jsonl
```

1行が1事象で、例外stack traceもJSON文字列内へ収める。Unity共通ログへは起動・切替の短い要約とwarning/errorだけを残す。専用ログは10 MiBを超えてLoaderが起動した時に`.1`へ1世代rotateする。

主要fieldは`ts / level / component / category / event / message / context / exception_type / exception`である。`event`は機械検索用のstableな識別子、`message`は人間向け説明、`context`には`road_id`、revision、入力path、mode、件数、処理時間等を入れる。

| category | 判断できる範囲 |
|---|---|
| `MOD` | Host自身のlifecycleまたは、より狭い原因へ分類できなかった内部例外 |
| `MOD_CONTRACT` | Loader/Runtime間のmethod契約不一致、未対応condition namespace等 |
| `DATA` | catalog/manifest/bundleを読み、適用を開始した正常な入力処理段階 |
| `DATA_MISSING` | catalog、manifest、bundle、texture、参照行等がない |
| `DATA_INVALID` | schema、revision、安全な相対path、mesh配列等の値が不正 |
| `CONTRACT_TYPE` | fieldは存在するがJSON型、enum、数値等の型契約が違う |
| `IO` | atomic置換中等で一時的に読めず再試行するfilesystem障害 |
| `CS1_ENVIRONMENT` | template prefabやshaderが未ロード、CS1がnode/segment生成を拒否した等 |
| `SUCCESS` | 検証、Prefab更新、既設renderer refresh、試験区画更新が完了した証拠 |

分類は原因を完全に断定するものではない。例えば`CS1_ENVIRONMENT`は「MOD外が悪い」ではなく、Hostの入力検証を通過した後のCS1状態・ロード順・他MODを含む境界で失敗したことを表す。`MOD`の未分類例外はコード不具合候補として扱う。

PowerShellでは次のように絞れる。

```powershell
$log = "$env:LOCALAPPDATA\Colossal Order\Cities_Skylines\Addons\Mods\RoadRuntimeHost\logs\RoadRuntimeHost.jsonl"
Get-Content $log | ConvertFrom-Json | Where-Object level -eq 'ERROR' | Format-List
Get-Content $log | ConvertFrom-Json | Where-Object { $_.context.road_id -eq 'jp-basic-2l' } | Format-List
```

同じmanifestでも未適用roadが残っていれば約1秒ごとに再試行する。同じrevision、分類、原因、例外messageの失敗は専用ログへ一度だけ記録し、入力または失敗内容が変わるまで重複行を抑制する。復旧時は`road_apply_recovered`を記録する。画面上の見た目が正しいこと自体はログだけでは証明しない。

## 更新動作

- Loaderは`runtime.current`を約1秒間隔で監視する。新しいhash名DLLがstage/installされるとRuntime実装を切り替える。
- Runtimeは`catalog.json`と`manifest.json`を約1秒間隔で確認する。
- revisionが変わったroadだけbundleを読む。
- `texture_revision`が変わると、共有source Texture2Dへ同じpathのPNGをin-placeで再読込し、共有APR/XYS Texture2Dもin-placeで再packする。道路bundleと既設segmentは作り直さない。
- catalogとBlender bundleのlane契約が一致しないroadは適用せず、同じrevisionでも修正されるまで再試行する。
- 同名Prefabがロード済みなら同じ`NetInfo` objectへmesh、material、lane、Prop配置を再設定する。既設道路を削除して引き直さない。
- mesh/materialだけの変更では試験区画を作り直さない。
- templateまたはlane metadataを含む構造signatureが変わった場合だけ、当該roadのHost所有試験区画を再生成する。
- catalog変更時はPropと配置、UI priorityが変わり得るため関連roadを再適用する。
- 失敗したroadはrevisionを完了扱いにせず、次回pollで再試行する。

## 新しく引く道路の線

対象道路をCS1の道路ツールで選択すると、画面右側に`New road lines`を表示する。
ここでは道路単位で次の3項目だけを選ぶ。

- `Roadside lines`: `On / Off`
- `Lane separators`: `White dashed / White solid`
- `Center line`: `Yellow solid / White solid / White dashed`

選択は同じ道路のGround、Elevated、Bridge、Tunnel Entrance、Tunnelで共有する。
道路定義の値は初期値であり、CS1での選択が次に作るsegmentへ優先される。
作成成功イベント時に値をsnapshotするため、その後panelを変更しても既設segmentは変わらない。
RuntimeHostは作成されたsegmentと両端nodeだけをIMTへ渡し、全segment走査や定期pollを行わない。
生成後の個別編集と保存値はIMTが所有する。

## Prop名とWorkshop公開後のID

道路からは公開ファイル名やWorkshop IDではなくcatalogの`prop_id`を参照する。`props.tsv`の`prefab_name`をローカル版または公開版の実際の名前へ差し替えるため、全道路の配置行を書き換えない。公開後の自動ID解決規則はまだ確認できておらず、現時点では表の一列を更新する運用である。

## 現時点で確定していないこと

- Runtime生成Prefabを含むmap saveが、全てのロード順序とMOD構成で再読込できるか。
- 既設道路に対してlane数を変えた際、CS1内部lane bufferが常に安全に再構築されるか。
- Adaptive Roads extension flagを実際のplugin APIへ書き戻すadapter。
- selectorごとに複数segment/node entryを展開する最終schema。
- Decalの`_DecalSize`、各map、Ploppable Asphalt相当material propertyの完全な表形式契約。
- 自動試験区画を保存したmapでの永続的な所有識別。現状は開発用の使い捨て確認mapを前提にする。

これらはデータを保持できる欄または拡張境界があっても、実ゲーム確認前に対応済みとはしない。

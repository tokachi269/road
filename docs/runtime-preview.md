# 通常マップRuntime preview

## 目的

Asset Editorを往復せず、Blenderで作った道路・Prop・Decal meshと表形式metadataを通常マップへ反映する開発用Hostである。最終CRPを作る機能ではない。

Runtimeで断面からmeshを生成しない。道路meshの正本はBlenderの生成後meshであり、手編集した頂点、法線、UVもそのままbundleへ入る。Runtimeが自動生成するgeometryはない。接続試験では、そのmeshを使うnetwork node/segmentの配置だけを生成する。

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

通常マップで使うlane metadataはcatalog側を優先する。Blender bundleにも単体preview用のlane snapshotが入るが、同じ`road_id`がcatalogにある場合は`lanes.tsv`がRuntimeの正本になる。mesh幅とlane表が一致するかをRuntimeが推測補正することはしない。

## Blender export

Road sidebarの`Runtime preview`で次を設定する。

1. `Runtime road ID`を`roads.tsv`の`road_id`と一致させる。
2. `Runtime prefab name`と`Template prefab`を設定する。
3. `Runtime output`をHostの`preview.path`と同じdirectoryへ向ける。
4. `Build and export runtime bundle`で全modeをbuildしてexportする。
5. 継続編集時は`Auto export every second`を有効にする。

auto exportは約1秒ごとにPropertyGroup、入力端部mesh、生成meshのfingerprintを比較する。設定が変わった場合は全modeをbuildしてから対象roadのbundleだけを置換する。生成meshを直接編集した場合は再buildせず現在meshをexportする。road bundleを書き終えてからmanifestをatomic replaceするため、Hostは途中のJSONを正規更新として読まない。

Prop/Decalは選択Meshを`Export selected Prop/Decal mesh`で出す。使用materialは現状1つだけに制限している。catalogの`mesh_bundle`へ`props/<prop-id>.json`を指定する。`textures`にはshader property名とpreview directory相対画像path、`material_properties`には`_DecalSize`等のfloatまたは2/4要素vectorをJSONで指定できる。値はRuntimeがmeshから推測しない。新規Prop登録には実在する`template_name`が必要で、未指定時にRuntimeが推測して適当なvanilla Propを選ぶことはしない。

座標はBlenderの`X=横、Y=道路長手、Z=上`からUnityの`X=横、Y=上、Z=道路長手`へexport時に変換する。preview配置用Object translationはmeshへ焼かない。

## buildとstaging

```powershell
Set-Location D:\GitHub\road
.\scripts\stage-runtime-host.ps1 -PreviewPath 'D:\GitHub\road\build\runtime-preview'
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

hash名の新DLLを先にコピーし、最後に`runtime.current`をatomicに置換する。Loader自体を変更した場合だけゲームを終了して再installする。

## 更新動作

- Loaderは`runtime.current`を約1秒間隔で監視する。新しいhash名DLLがstage/installされるとRuntime実装を切り替える。
- Runtimeは`catalog.json`と`manifest.json`を約1秒間隔で確認する。
- revisionが変わったroadだけbundleを読む。
- 同名Prefabがロード済みなら同じ`NetInfo` objectへmesh、material、lane、Prop配置を再設定する。既設道路を削除して引き直さない。
- mesh/materialだけの変更では試験区画を作り直さない。
- templateまたはlane metadataを含む構造signatureが変わった場合だけ、当該roadのHost所有試験区画を再生成する。
- catalog変更時はPropと配置、UI priorityが変わり得るため関連roadを再適用する。
- 失敗したroadはrevisionを完了扱いにせず、次回pollで再試行する。

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

# Road generator architecture

> 文書種別: 現行構成

この文書は、現在の実装に存在するownerと依存方向を示す。
詳細な寸法、UV、Runtime連携は[実装詳細](reference/implementation-details.md)に置く。
候補案や比較結果は[設計判断](design-decisions.md)に置く。

## State ownership

道路定義は、順序付きlane、共有断面、mode固有構造、表示設定に分ける。
Blender UIとJSONは編集adapterであり、同じ判断を独自に実装しない。

| 判断 | 現在のowner | 主なconsumer |
| --- | --- | --- |
| laneの順序、幅、方向、CS1 metadata | 道路定義のlane | 断面導出、Runtime bundle |
| 歩道間幅、歩道幅、分離帯 | 共有断面 | 路肩、駐車lane、mesh |
| 路肩幅と駐車lane | `domain.py`の導出結果 | Blender UI、mesh生成 |
| 車道の0.30 m低下 | `domain.py`の固定値と道路単位の設定 | 全mode |
| 道路線の3規則 | 道路定義の既定値とCS1配置時の道路単位選択 | boundary style、新規segmentのIMT初期値 |
| atlas領域とUV profile | `textures/dimensions.json` | Blender、texture検証 |
| Blender preview mesh | 生成後のBlender mesh | Runtime preview bundle |
| CS1向けentry | Output Entry Plan | RoadImporter、RuntimeHost |

`layout.strips`はcompiled topologyであり、編集正本にしない。
現行schema v3にはlaneとstripの重複が残るため、最終schemaとはみなさない。

## Derived topology

`blender_addon/road_builder/domain.py`がBlender非依存の判断を所有する。
lane adjacency、boundary ID、断面幅、marking roleを下流で再推論しない。

- laneはstable IDを持つ。
- boundary IDは隣接するstable lane IDから導出する。
- lane追加時は、隣接関係が変わったboundaryだけを置換する。
- 路肩は歩道間幅、lane合計幅、分離帯幅から導出する。
- 寸法が収まらない場合は入力を黙って縮めず、Blenderで警告する。

## Mesh registration boundary

Blenderはgeometryを生成し、RoadImporterとRuntimeHostはCS1向けentryへ変換する。
RoadImporter XMLとRuntime bundleを編集正本にしない。

`surface`、`structure`、`tunnel`はgeometryを整理する呼称である。
CS1の登録単位はselector、shader、texture setも含めて決める。
呼称だけを理由にmesh entryを分割しない。

BlenderのSceneは複数の道路定義をCollectionPropertyとして保持し、sidebarの一覧で
選択した1件だけを既存の断面・線・mode編集UIとpreview生成へ渡す。
JSONとTSVは個別・一括編集用の入出力であり、Blender一覧を自動監視して同期しない。
ファイルとの不一致はimport、export、Runtimeの既存validationで明示し、UI内に別の
catalog正本や同期frameworkを追加しない。

現在のBlender previewはmodeごとにsegmentとnodeを生成する。
最終的なentry数、LOD、selector構成はゲーム内検証が終わるまで固定しない。

## UV ownership

UV座標は`textures/dimensions.json`から生成する。
PSDと生成PNGは画像制作物であり、座標の正本ではない。

- 制作atlasは2048 pxを基準とする。
- 1024 pxへの縮小時も正規化UVを維持する。
- line slotは32 px単位だが、faceは各regionの実幅を使う。
- 任意指定meshのUVは保持する。
- missing UVを0から1の投影で補完しない。

固定値とprofileはtexture layout testで独立に検証する。
詳細なregion配置とchannel packingは[実装詳細](reference/implementation-details.md)を参照する。

## Dependency direction

```text
road recipe / ordered lanes
        ↓
Blender-independent domain decisions
        ↓
derived topology and mesh plan
        ↓
Blender mesh adapter
        ↓
FBX + RoadImporter XML / Runtime bundle
        ↓
CS1
```

下流は上流の判断を再実装しない。
`road_builder.domain`はBlender、Unity、RoadImporterへ依存しない。
`tools/arch_manifest.json`と`tools/arch_lint.py`が依存方向の一部を検査する。

## Runtime preview boundary

Runtime previewはAsset EditorとCRP生成とは別の開発経路である。
Blenderで生成したmeshを通常マップへ反映するが、Runtimeで断面meshを再生成しない。

IMTは生成後の線、停止線、ゼブラとユーザ編集を所有する。
RuntimeHostはmissing時だけ初期値を作り、定期pollで復元しない。
CS1の道路ツールでは、対象道路を選択中だけ次に引く道路の3規則を表示する。
選択値は`CreateSegment`成功時にsnapshotし、そのsegmentと両端nodeだけへ適用する。
既設道路の全segment走査と定期pollは行わない。

Runtimeの入出力、hot reload、IMT連携の詳細は
[Runtime preview](runtime-preview.md)と[実装詳細](reference/implementation-details.md)を参照する。

## 未確定事項の扱い

未確定事項はこの文書で契約化しない。
比較中の案は[設計判断](design-decisions.md)、過去の経緯は[履歴](handoff.md)へ置く。
確定するにはowner、consumer、失敗時の扱い、実行可能な検証が必要である。

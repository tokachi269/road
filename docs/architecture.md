# Road generator architecture

この文書は現在の実装で実在する責務境界だけを定める。試作中のschemaや未実装のmesh構成を、lintによって先に確定しない。

## State ownership

現在のBlender prototypeでは、順序付きlane、共有断面値、mode固有構造値、marking設定からpreviewを生成している。

- Laneはstable ID、順序、幅、方向、CS1 lane metadataを所有する。
- 歩道幅、路肩幅、curb高さは共有断面値が所有する。
- 現在の線設定は、導出されたboundary IDに対応付けている。
- Blender PropertyGroupは編集adapterであり、別の意味を決めない。
- RoadImporter XMLはCS1向けcompiled outputであり、編集正本にしない。

現在のschema v3は`lanes`と`layout.strips`へ幅と接続を重複して保存しているため、最終契約ではない。どちらを正本にするかは未決定であり、比較評価は`docs/design-decisions.md`に置く。既存round-tripを壊さずに方針を決めるまでは、この重複へ新機能を追加しない。

## Derived topology

現在のprototypeでは、stripとboundary topologyを順序付きlaneと共有断面値から導出している。

- 車道strip IDはstable lane IDから導出する。
- Lane間boundary IDは左右のstable lane IDから導出する。
- Curbとcarriageway edgeは意味上の固定IDを使う。
- Lane追加時に隣接関係が変わったboundaryだけを置換し、無関係なedge IDを維持する。
- Boundaryのrole既定値とmarking role既定値はdomain ownerが決める。

この現行挙動の実装ownerは`blender_addon/road_builder/domain.py`である。Blender UIはその結果を表示し、保存済みmarking設定を同じIDへ戻す。最終schemaで同じ所有モデルを採用するかはまだ固定しない。

## Mesh registration boundary

現在はmodeごとにsegment/node各1 objectを生成し、surfaceと構造を混在させている。RoadImporter側はsegment/node mesh配列を持ち、entryごとにshaderとtextureを指定できる。

連携会話では次の3 familyへ分ける案が出た。

| family | 内容 | 使用mode |
|---|---|---|
| `surface` | asphalt、pavement、curb、路肩、線用band | 全mode |
| `structure` | 高架・橋の床版下面、側面、主桁等 | Elevated、Bridge、必要なTunnel Entrance |
| `tunnel` | tunnel側壁、天井等 | Tunnel、必要なTunnel Entrance |

この分割はまだproduction codeにもarchitecture lintにも入れない。「一体mesh」が道路全体で1 objectを意味するのか、RoadImporter entryごとに一体であればよいのかも、ユーザー要求とImporter実測を合わせて確定する必要がある。

線を`surface`内のface regionへ残す案、node専用atlasを作らない案も比較候補であり、未確定である。

主桁を道路方向へ短い部品Arrayするのではなく、横断面へ複数の断面profileを置いて長手方向へ押し出す案は、ユーザーの意図と一致する有力候補である。ただしstructure entryの分割と桁本数規則は未確定である。

現在のBlender previewでは、国交省資料に掲載されたJIS A 5373 PCコンポ橋の標準桁間隔2.6/3.2/3.8mから、床版幅に収まり外側余白が標準断面に最も近い偶数本の構成を選ぶ。自由寸法入力は設けない。桁高は確認用支間35mの標準値1.8/2.1/2.5m、矩形近似の幅は標準主桁断面の下フランジ全幅0.70mを使う。

Elevatedの左右端部は、Blender内の右側基準Mesh Object 1つで固定断面fasciaを置換できる。Object原点のX/Zを右路面外角anchorとし、右はそのまま、左はX反転と面頂点順の反転を行って配置する。Z<0だけを床版厚へ追従させ、最下の長手辺を床版下面の端へweldする。通常面はsegment/nodeのslice位置で分割し、`CS1_NO_SPLIT` vertex groupの頂点に触れる面は柵等の剛体部として分割しない。指定時も出力はmodeごとのsegment/node各1 objectのままである。入力Object参照はBlender adapterの編集状態であり、未確定のschema v3へ追加しない。

## UV ownership

現在の実装は、明示要求に従い歩道上面、curb立面、路面を連続した断面展開にし、線bandはalpha余白を含む独立0～1領域を使う。目標はcurb壁の接続位置を両側で一致させ、壁面用の幅を確保した上で領域ごとのscaleを調整することであり、全領域を同じscaleへ固定することではない。

別会話で提案されたsemantic boundaryごとのUV seamは、現在の明示要求と衝突する可能性がある。道路幅でtexture scaleが変わる問題もあるため、どちらもlintで固定せず実ゲーム比較の観測対象にする。

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
FBX + compiled RoadImporter XML
        ↓
CS1 Asset Editor
```

下流はlane adjacency、boundary role、幅を再推論しない。`road_domain`はBlender、Unity、RoadImporterへ依存しない。Architecture manifestと`tools/arch_lint.py`がこの依存方向の一部を機械検査する。

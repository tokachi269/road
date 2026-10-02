# CS1道路生成仕様

> 文書種別: 仕様資料

この文書は、RoadImporter、サンプルアセット、CSUR、プロジェクト判断を区別する資料である。
実装済みかどうかはコードとテストで確認する。現在のownerは[構成と責務](architecture.md)、
検証方法は[検証方針](testing.md)を参照する。

## 根拠の区分

- **Importer契約**：`RoadImporter`のC#データモデルに存在する仕様。
- **サンプル確認済み**：`RoadImporter/sample.zip`の`CSUR 8DR`で観測した仕様。
- **CSUR慣例**：CSURでは使用されているが、CS1全体の必須仕様とは断定できないもの。
- **本プロジェクトの決定**：この独自ジェネレータで採用する仕様。
- **未実装**：ゲームへ完成品としてインポートするには必要だが、まだ出力していないもの。

## ジオメトリ契約

| 要件 | 根拠・状態 | 実装規則 |
| --- | --- | --- |
| Segmentの長手方向寸法 | サンプル確認済み・本プロジェクトの決定 | 通常のsegmentメッシュはローカル長手軸の-32 mから+32 mまで、正確に64 mとする。CSURには62.5 mの特殊なshift segmentがあるため、64 mをCS1エンジン全体の絶対制約とはみなさない。 |
| Nodeの長手方向寸法 | サンプル確認済み・本プロジェクトの決定 | 生成するnodeメッシュは正確に64 mとする。サンプルのnode asphaltも約64 mである。 |
| Segmentのカーブ用分割 | サンプル確認済み・本プロジェクトの決定 | 全モードのsegmentを長手方向に20分割する。頂点列は21列、間隔は3.2 m。CSUR Ground laneサンプルも同じ20分割であり、実行時のbend変形で一本の直線quadではなく曲線を構成するために必要。 |
| Nodeの分割 | サンプル確認済み・本プロジェクトの決定 | nodeは長手方向に8分割する。頂点列は9列、間隔は8 m。確認したCSUR Ground node asphaltと一致する。X=0での中央分割とは別の要件である。 |
| 座標系 | サンプル確認済み | BlenderへFBXを読み込んだ状態で、Xが道路横断方向、Yが長手方向、Zが高さ方向。 |
| メッシュの単位 | 本プロジェクトの決定 | 各モードにつき、segmentを1個のBlenderメッシュオブジェクト、nodeを1個のBlenderメッシュオブジェクトとして生成する。CS1のselector別メッシュとLODはエクスポート時に生成し、previewをプリミティブの集合にはしない。 |
| 不可視面 | 本プロジェクトの決定 | Groundは路面上面、歩道上面、露出するcurb立面だけを持つ。ElevatedとBridgeは見える下面と外側fasciaを追加するが、長手方向の端面は作らない。Tunnelは路面と内向きの壁・天井を持つ。 |
| Node中央分割 | ユーザー要件に基づく本プロジェクトの決定 | 1個のBlenderオブジェクトを維持したまま、X=0の路面頂点を共有しない左右のvertex islandにする。左右を独立したトポロジーとして扱え、プリミティブオブジェクトへ分解する必要はない。 |
| UV map | サンプル確認済み・Blender preview実装済み | 確認したすべてのCSURサンプルFBXにUV layerが1個ある。通常道路端は歩道上面、curb壁、既存路面の3面を維持し、texture区分のためにcurb上面・下面の細いfaceを追加しない。生成structureは床版下面、mode別fascia、主桁側面・下面、生成tunnelは壁・天井を各familyのregionへ割り当てる。curb立面を上面投影で潰さず、左右は同じregionを反転利用する。64 mの長手方向は0～1で生成し、Blender Material側でVを2倍して2048px内の16m周期を反復表示する。線の中央に余分な辺は置かない。指定meshの入力UVは保持する。最終FBX/CRPで同じtexture scaleが復元されることは未検証。Diffuseは`_MainTex`、a/p/rは`_APRMap`、n/sは`_XYSMap`へ変換するRuntime preview契約とchannel packingは実装・contract test済みだが、ゲーム内表示は未検証。 |
| LOD | サンプル確認済み・未実装 | 参照したすべてのsegment/node FBXに対応する`_lod.FBX`がある。LODの生成とエクスポートが完了するまでgame-readyとは扱わない。 |
| Normal | Importer・エクスポート要件 | 可視面は意図した頂点順序で生成する。隣接するsegment/nodeが覆う端面は省略する。Blender上でnormalとback-face visibilityを検証する。 |

RoadImporterのサンプルFBXをBlender 5.1へ読み込み、実測した結果：

| サンプルメッシュ | X幅 | Y長 | オブジェクト数 | UV layer数 |
| --- | ---: | ---: | ---: | ---: |
| `CSUR 8DR_basic.FBX` | 51.500 m | 64.001 m | 1 | 1 |
| `CSUR 8DR_glanes.FBX` | 44.000 m | 64.000 m | 1 | 1 |
| `CSUR 8DR_g_node_asphalt.FBX` | 43.600 m | 64.000 m | 1 | 1 |
| `CSUR 8DR_elevated.FBX` | 35.625 m | 64.953 m | 1 | 1 |
| `CSUR 8DR_slope.FBX` | 35.675 m | 64.000 m | 1 | 1 |
| `CSUR 8DR_tunnel.FBX` | 35.625 m | 64.000 m | 1 | 1 |

Elevatedサンプルは構造物の可視ディテールが端から張り出すため、raw meshのbounding boxが64 mを超えている。一方、NetInfoの`m_segmentLength`は64である。したがって、論理上のsegment長とメッシュのbounding boxは別々に検証する。

## 全モードで共有する道路定義

次の値は道路全体の定義であり、Ground、Elevated、Bridge、Tunnel Entrance、Tunnelの間で変更しない。

- 左から右へ並ぶlane一覧と、laneごとの幅。
- 左右それぞれの路肩幅。
- 左右それぞれの歩道幅。
- Curb高さ。
- 分離帯の有無、全幅、路面からの高さ、生成curbまたは基準Mesh。
- 路面profile：地面・歩道と同じ高さ、またはcurb高さ分だけ低い状態。
- 通常segmentの64 m長とnodeの64 m長。
- Segmentの20分割とnodeの8分割。

全モードのpreviewで同じ横断面を使用する。低い路面profileを選択した場合、Elevated、Bridge、Tunnel Entrance、Tunnelでも路面を歩道よりcurb高さ分だけ低くする。モード固有の高さは断面全体の配置を変えるものであり、物理的な横断面定義は変更しない。

`curb_width`は設けない。curbは路面と歩道面の間に露出する境界立面であり、独立したbox状の帯ではない。

## Lane契約

`lane_count`だけでは不十分である。`RoadImporter.CSNetInfo.Lane`が持つ次の情報を道路仕様で保持する。

| Lane項目 | 根拠 | Editorでの状態 |
| --- | --- | --- |
| 横断方向の順序・位置 | Importer契約 | 現在はlaneの並び順と幅から位置を計算する。明示的なoffset指定は今後の拡張。 |
| 幅 | Importer契約 | Laneごとに編集可能。 |
| Direction / final direction | Importer契約 | `Forward`、`Backward`、`Both`として編集可能。 |
| Lane type | Importer契約 | `Vehicle`、`Pedestrian`、`Transport Vehicle`、`Parking`、`None`を編集可能。 |
| Vehicle type | Importer契約 | CS1で一般的に使用するvehicle typeを編集可能。 |
| Speed limit | Importer契約 | CS1内部scaleで編集可能。 |
| Vertical offset / stop offset | Importer契約 | 編集可能。 |
| Allow connect | Importer契約 | 編集可能。 |
| Terrain height / center platform / elevated | Importer契約 | 最終exporterで保持する。現在のpreview editorには未公開。 |
| Lane props | Importer契約・未実装 | Prop・tree、およびrequired/forbidden flag用のeditorとexport処理が必要。 |

サンプルのCSUR 8DR Groundには13 laneがある。内訳はcar laneが8本、bike laneが2本、pedestrian laneが2本、中央の`None` laneが1本。したがって「lane数」をcar laneの合計だけとして扱ってはならない。

## NodeとTransitionの契約

- 標準nodeの手前側端部をsegment端部へ接続する。
- Groundでは、nodeの接続先端部を同じprofileのままにするか、64 m全体を使って地面同高とcurb深さの間で遷移させる。
- Transitionでは路面自体を傾斜させる。歩道は地面高さを維持し、露出するcurb立面の高さを連続的に変化させる。
- X=0の中央分割をnode全長にわたって維持する。
- Nodeにはsegment用の線領域・lane境界分割・線materialを生成しない。路側帯境界だけは`shoulder_bands`で選択可能とし、OFF時は道路両端と必須のX=0だけで路面を分割する。
- CS1のnodeメッシュ選択はflagで制御される。Importerはrequired node flags、forbidden node flags、connect group、direct-connect、transparencyを扱える。
- CSURはnormal、`Transition`、`End`、`TrafficLights`、direct-connectごとに異なるselectorを使用する。これはエクスポート時に必要なvariantであり、Blender上のオブジェクト数とは区別する。
- CSURのcompatibility nodeは64 mのslopeと固定値-0.15/-0.30 mを使用する。この値はCSUR慣例であり、本独自ツールの既定値にはしない。

## モード別契約

| モード | Mesh previewの責務 | Importer・runtimeの責務 |
| --- | --- | --- |
| Ground (`basic`) | 路面、歩道、見えるcurb立面。凹み道路の通常nodeは路面高-0.3 mを維持し、平らな道路側だけ必要に応じて0 mから-0.3 mへ下る64 m node profileを持つ。 | Terrain clipping/flattening、pavement生成、corner offset、traffic light、intersection node selector。 |
| Elevated | 指定高さにある開放型deckの上面・下面・fascia。 | Elevated AI、pillar定義とoffset、elevation cost。 |
| Bridge | 指定高さ・厚さにある開放型deckの上面・下面・fascia。 | Bridge AI、bridge pillar、lower-terrain、twist、bendingの挙動。 |
| Tunnel Entrance（内部キーは`slope`） | Ground高さとTunnel高さを結ぶ64 mの長手方向transition。 | Slope AIおよびforward/invert mesh selector。CSURの実装から、特に非対称道路では上り・下りmeshをforward/invert flagで選択することが確認できる。 |
| Tunnel | 指定深さ・建築限界の路面と内向きtunnel envelope。 | Tunnel AI、underground/transition vehicle flag、Tunnel固有のsegment selector。 |

UI上の名称は**Tunnel Entrance**とする。`slope`は`RoadAssetInfo`で使われるCS1内部mode keyとしてのみ残す。

## 生成が必要なImporterデータ

完成パッケージはFBXだけではない。`RoadAssetInfo`は5個のNetInfo、5個のAI、5個のmodel sectionを持つ。使用する各モードについて、次を生成できる必要がある。

- NetInfoの寸法と挙動：`m_halfWidth`、`m_pavementWidth`、`m_segmentLength`、高さ・勾配・角度制限、terrain処理、bending、collision、connect group、vehicle flags。
- 順序と全metadataを保持したlane一覧。
- Forward/backwardのrequired/forbidden flagと対応付けたsegment mesh entry。
- Node flag、connect group、direct-connectと対応付けたnode mesh entry。
- Shader、color、texture key、FBX名、indexを持つmodel entry。
- Cost、noise、highway rule、traffic light、およびモード固有pillar情報を持つAI。

モード別のNetInfo・AI値はruntime挙動を記述するため、モードごとに異なってよい。Lane幅やcurb高さなどの物理的な横断面値は全モードで共有する。

## バリエーション量産の方針

このジェネレータの目的は、2車線、3車線、4車線、路側線の有無などを個別にモデリング・描画せず、同じ定義と素材から自動生成することである。

CS1ではlane metadata、NetInfo、mesh selectorが道路prefabごとに固定されるため、2車線道路と4車線道路は最終的には別アセットになる。ただし、FBX・UV・XML・thumbnailを手作業で作り分けず、parameter JSONからbatch生成する。

### 生成器が行うこと

- Lane、路肩、歩道、curb、側端profileから横断面meshを生成する。
- Segmentを20分割、nodeを8分割し、カーブ変形可能なmeshを生成する。
- 共通texture atlasのどのtileを使うか決め、UVを割り当てる。
- Lane metadata、selector、NetInfo、AI、FBX、LOD、thumbnailを出力する。

### 生成器が行わないこと

- 車線数や線の組み合わせごとに、新しい道路texture全体を描画・合成しない。
- 白線・側溝・curbを別々のBlenderオブジェクトへ分解しない。
- 見えない厚み、裏面、端面を追加しない。
- 初期段階では道路間のtexture runtime共有専用loaderを作らない。

## Meshとtextureの使い分け

判断基準は「高さ・輪郭・影・接続形状を変えるか」である。変えるものはmesh、表面模様だけのものはtextureにする。

| 要素 | 表現方法 | 規則 |
| --- | --- | --- |
| Asphalt、concrete、歩道舗装 | 共通texture atlas | 車線数ごとの画像は作らず、面のUV範囲を変える。 |
| 車線境界線、中央線、路側線 | Textureを割り当てた細い面帯 | Solid、dashed、double、white、yellow、線なしのtileを用意する。線なしはasphalt tileを使う。別materialや別objectにはしない。 |
| Stop line、横断歩道 | Node用atlas tileまたは必要時だけ生成するoverlay面 | 道路全長textureへ焼き込まない。Node selectorに応じて生成する。 |
| 矢印・文字・特殊road marking | Lane propまたは条件付きoverlay面 | 常時segment meshへ入れない。Asset依存propを必須にはしない。 |
| Curb | 高低差はmesh、表面はatlas | `curb_height`を断面meshへ反映する。独立した`curb_width`やboxは作らない。 |
| 平坦な側溝、排水帯、grating模様 | Texture | 路面と同じ高さで輪郭が変わらない場合。必要に応じてnormal・specular系mapを追加する。 |
| 浅いcurb gutter | 断面mesh＋共通texture | 数cmの落ち込みでも、路面高さと水切り形状を表現する場合は断面profileを作る。長手方向へ同じprofileを押し出す。 |
| 開渠、U字溝、V字溝 | Mesh＋共通texture | シルエット、影、terrainとの境界が変わるためmeshにする。断面点列から生成し、個別モデルは作らない。 |
| 小さな排水穴・grate | 原則texture | 遠景で判別できず、繰り返し数が多いものはpolygonにしない。 |
| 大型grate、guardrail、barrier、pillar | Meshまたはlane prop | 輪郭が変わるものだけ。繰り返し配置できるものはlane propを優先する。 |

### Road markingのmesh構成

路面はlane幅の大きなquadだけでなく、lane境界位置にmarking領域を持つ単一meshとして生成する。Marking領域も路面と同じtexture atlas・materialを使い、UV tileだけを切り替える。

Marking領域幅を塗装線の公称幅と同じにしてはならない。15 cmの線に15 cmの面を割り当てると、剥がれによるalpha境界、anti-aliasing、線の左右揺れが面端で切れる。Texture tileには線と周囲のasphalt余白を含める。

Marking領域幅は次で決める。

`marking_region_width = paint_width + 2 × (max_lateral_wander + edge_wear_margin)`

初期値は次を基準とする。

- 公称15 cmの通常線：marking領域30～40 cm。
- 左右揺れを大きくする劣化線：marking領域40～50 cm。
- 二重線・斜線帯：実際の線群の外幅に左右それぞれ10～15 cm以上のasphalt余白を加える。

Marking領域の境界pixelは周囲のasphalt tileと同じ色・normal・roughnessへ収束させ、面の継ぎ目を見せない。剥離形状と線の揺れはtile内のmaskで表現する。Marking領域は線中心で二分せず、両端だけを持つ1枚のface帯にする。帯の横UVは、atlas全体では各線の26 px content region、線素材内ではその全幅を使い、線中心はtexture内で表現する。32 px slot全幅は使用しない。長手方向はNetwork materialのV scale `0.5`を基準に2反復相当とし、情報量が不足する場合は同じ面のV範囲を広げる。周期変更のためにmarkingを別meshへ分離しない。

通常道路端は歩道上面1枚、curb壁、既存路面faceの3面とする。`curb.upper`と`curb.lower`はtexture制作上の素材区分であり、その幅に合わせた専用faceや横断辺を追加しない。歩道上面は`sidewalk.default`と`curb.upper`の定義px幅を合計したUV幅を持ち、内側共有辺を`curb.wall`上端へ一致させる。壁に接する最外側路面は`curb.lower`と接しているasphalt regionの定義px幅を合計し、外側共有辺を`curb.wall`下端へ一致させる。形状幅がtexture制作幅と異なる場合は、この合成UV幅を1枚のfaceへスケールする。共有する形状頂点をUV境界のために分離せず、curb立面を上面投影で潰さない。Ground node transitionのようにcurb高さが長手方向で変化する場合も同じ規則を使う。生成curb付き分離帯は別仕様で、curb上面と中央上面を分ける。

この方式では、路側線の有無を次のように変更できる。

- 路側線あり：外側marking領域を`solid_white` tileへ割り当てる。
- 路側線なし：そのboundaryにはmarking領域を生成せず、通常asphalt面を隣の境界まで連続させる。
- 破線：境界のmarking領域を`dashed_white` tileへ割り当て、長手方向UVを一定周期で繰り返す。
- 中央二重線：二重線全体とasphalt余白を覆う領域を作り、`double_white`または`double_yellow` tileへ割り当てる。

線の種類が増えてもmesh generatorの分岐はtile名の選択だけで済み、車線数×線種のtexture組み合わせを作らずに済む。

### 現在実装済みのmarking範囲

- Segmentの各boundary単位で線をON/OFF。
- Boundaryごとの意味を`CARRIAGEWAY_EDGE`、`CENTER_LINE`、`LANE_SEPARATOR`として保持。
- Paint幅とmarking領域幅の指定。既定値は15 cmと40 cm。
- Marking領域を含む単一mesh、1 UV layerの生成。
- 線OFFの位置にはmarking領域自体を生成せず、不要な横断方向の辺を減らす切り替え。
- Blender preview用の中央15 cm線表示。
- JSONへの設定保存と読み込み。
- Nodeは線領域を持たず、路側帯境界の分割だけを個別にON/OFF可能。

現段階では線種はsolid whiteだけとする。Dashed、double、yellow、劣化mask、最終atlas画像、node markingは仕様上の方針だけを保持し、要求されるまで先行実装しない。

## 操作モデルと設定の所有

`wire`リポジトリの`domains/wire`から採用する中心はUI配置ではなく、入力、保存する正本、派生結果を分離し、stable IDで関係を保持する考え方である。道路固有のstrip・lane・boundary・marking契約は同リポジトリの`domains/road`も参照する。

### 正本の分割

| 正本 | 所有する値 | 所有しない値 |
| --- | --- | --- |
| `strip` | Stable ID、左から右の順序、`SIDEWALK` / `SHOULDER` / `CARRIAGEWAY` / `MEDIAN`、物理幅、surface style | 通行方向、CS1 lane type、線の種類 |
| `lane` | Stable ID、使用する`surface_strip_id`、strip内の横範囲、方向、CS1 lane/vehicle type、speed、offset、接続可否 | 路面全体の幅、隣接線の所有権 |
| `boundary` | Stable ID、左右のstrip ID、`CURB` / `CARRIAGEWAY_EDGE` / `LANE_DIVIDER`、profile、任意のmarking policy | Lane metadata、texture画像そのもの |
| `marking style` | Stable style ID、paint幅、asphalt余白を含むregion幅、texture tile | 線を置く位置、接続先 |
| `mode` | Elevated高さ、deck厚、Tunnel深さなど配置・構造固有値 | Lane幅、歩道幅、curb高さ、線設定 |
| `node` | 中央分割、路側帯境界の有無、Ground profile transition | Segmentのboundary marking |

線はlaneに所属させない。自動の長手方向線は`boundary.marking`に所属する。Laneの左右に線を付ける操作を将来用意する場合も、それは入力意図であり、保存前に隣接boundaryへ解決する。同じboundaryへ複数laneから矛盾する線要求が来た場合、暗黙の優先順位ではなくvalidation errorにする。

Laneとboundaryの配列indexはidentityではない。Lane追加、削除、並べ替え、2車線から3車線へのtransitionではstable IDで継続・出生・消滅を判断する。線も横位置の近さではなくboundary IDで対応付ける。

### 保存しない派生値

次は正本から毎回導出し、JSONへ重複保存しない。

- Lane中心位置、道路総幅、half width。
- Boundaryとmarkingの横位置。
- Segment/nodeの頂点、face、UV、material slot。
- 64 m分割後のサンプル位置。
- 生成済みpreview object名や現在選択中のUI index。分離帯の`mesh_object`は生成結果ではなくBlender-localな入力Mesh参照として現行v3へ保存するが、他ファイルでも通用するasset identityとは扱わない。

これによりlane幅やstrip順序を変更したとき、古い`position`と新しい幅が食い違う状態を作らない。

### プリセットと操作

- 2・3・4車線presetは`strip + lane + boundary`の初期値を一度作る入力であり、preset名を道路の正本にしない。
- Lane追加はlaneだけをappendする処理ではない。Carriageway strip、Lane、左右boundaryを一回の操作で作り、検証後にまとめて確定する。
- Lane削除・並べ替えでは、影響するboundaryだけを再構成する。同じstable boundary IDが残る線設定は維持する。
- 線のON/OFFや種類変更はboundaryだけを編集し、lane metadataや物理幅を変更しない。
- Marking styleのpaint幅やregion幅を変更すると、そのstyleを参照する全boundaryへ反映する。Boundaryごとに同じ15 cmを重複保存しない。
- Node markingはsegment boundary markingから暗黙コピーしない。CS1 node selectorに必要になった時点でnode所有の別設定として追加する。

### Schema version 4と現在の対応範囲

JSON v4は`shared_geometry`、`styles`、`layout.strips`、`layout.boundaries`、`lanes`、`node`、`modes`へ分離し、共有profileは別の`profiles.json`へ置く。laneの速度、停止offset、接続可否はglobal、profile、road overrideの3段階だけで解決する。Blender UIでは道路単位の3規則からboundaryの線を導出し、個別線は編集しない。旧v2の`edge_lines` / `lane_lines`は互換入力、v3の展開済みlane値は明示overrideとして読み込み、v4では展開値を保存しない。

現版の生成器は、車道lane 1本につきcarriageway strip 1本、左右対称の歩道幅・路肩幅、最初の対向方向boundaryへ置く分離帯、`SOLID_WHITE` styleに対応する。分離帯は全幅・路面からの高さを共有設定とし、生成curb囲いまたは64m基準Meshを選ぶ。生成curbでは左右curb上面と中央上面を別faceにし、中央上面を分割しない。基準Meshは設定幅・高さへfitして長手sliceへ切り、両端capを除く。V4が表現できてもBlender editorが安全に編集できない左右非対称幅は、黙って丸めずimportを拒否する。複数laneを1 stripへ割り当てる構成、線placement、transitionのID対応は次の実装範囲であり、対応済みとは扱わない。

### Blenderでの提示

- Scene内の道路一覧を最上段に表示し、道路名、Runtime road ID、車道／歩道lane数、総幅、中央分離帯の有無を選択前に確認できるようにする。
- 一覧は道路の追加、複製、削除を提供し、選択中の1件だけを下の編集panelとpreview生成へ渡す。道路切替えのためにJSONやTSVを自動監視しない。
- JSONはactive roadの置換、別roadとしてのimport、active roadのexport、全roadの一括exportに使用する。TSVは表計算ソフトによる一括編集とRuntime入力に引き続き使用し、Blender UIの一覧正本にはしない。
- 順序付きlaneを断面表へ表示し、線は道路単位の3規則で編集する。
- Lane欄は交通・CS1 metadataと値のsourceを表示し、明示overrideの設定と解除を行う。
- Stable IDは通常操作で直接入力させず、確認用に表示する。
- 横断面previewはstrip幅に比例させる。これは未実装。

### 標準車両lane family

車両lane数は方向別に0〜4、両方向合計で最大8とする。左右反転で同一になる
組合せは同じPrefabを道路反転で使用するため、標準familyは次の14種類とする。

- 一方通行: `0+1`、`0+2`、`0+3`、`0+4`
- 対面通行: `1+1`、`1+2`、`1+3`、`1+4`、`2+2`、`2+3`、`2+4`、`3+3`、`3+4`、`4+4`

ここでlane数に歩道lane、駐車lane、将来の自転車laneは含めない。標準variantは
3.0 m幅のvehicle lane、左右各0.5 mの路肩、左右歩道を持つ。中央分離帯、駐車、
専用laneはこの14種類へ無条件に直積せず、実在する追加variantが必要になった時点で追加する。

## Texture共有方針

すべてを1枚へ詰め込まず、用途単位の少数atlasに分ける。

1. `road_surface`：asphalt、lane marking、路肩、歩道、curb、平坦な側溝。
2. `structure`：Elevated・Bridgeの下面、fascia、barrier、concrete・steel。
3. `tunnel`：Tunnel壁、天井、portal。
4. `node`：交差点asphalt、横断歩道、stop lineなど、node固有のUVが必要な場合だけ使用。

各atlasは全車線数・全道路variantで再利用する。2048×2048を制作基準とし、1024×1024へ半減してもregion幅が整数になる偶数幅で配置する。通常regionの座標は偶数とするが、32 px slot内の26 px line bandは中央を合わせるため左右3 px paddingとし、開始座標は奇数を許容する。1024版ではその境界が半画素になるため、UV座標を整数へ丸めず正規化位置を維持する。Network materialのV scaleは`0.5`、長手方向は16 m周期を2反復した32 mとして扱う。破線は6 m塗装＋10 m空白を制作基準とする。これにより2048版は縦横64 px/m、1024版は32 px/mになる。L形側溝250Aの制作ベースは立面だけでなく、歩道側の100 mm天端と車道側の250 mm排水面も別PNGレイヤーとして持つ。curb profileは上面6 px、将来0.15 m想定の壁面10 px、下面16 pxを連続した32 pxとして置き、外側に2048版32 pxのpaddingを持つ。現行preview geometryの0.30 m高とtexture制作上の0.15 mは同一値として扱わない。UV位置は`textures/dimensions.json`の各content regionから生成し、PSDやBlenderへ別々に手入力しない。固定slotは素材配置の管理単位であり、face UVへは使わない。解像感が不足する場合は、まず同一mesh上のV使用範囲を広げ、それでも不足したatlasだけ解像度を上げる。LODには小さい共通atlasを用意する。Diffuseは必須とし、Alpha、Pavement、Road、Normal、Specularは表現上必要なfamilyだけ追加する。

Blenderで編集するsegment/nodeはそれぞれ1個のmesh objectを維持する。ただしCS1の1個のsegment mesh entryは基本的に1material・1shaderとして扱われるため、エクスポート時だけshader単位へ分ける。Material分類は共通の`surface / structure / tunnel`とし、1 modeの出力はsurfaceと対応する非surfaceの最大2系統にする。Slopeの非surfaceはtunnelへ含め、白線、curb、側溝ごとのrender meshやdraw callは増やさない。

RoadImporterの現在の`optLevel = 0`では、同じsource atlasを使ってもtextureは各CRPへ格納される。これは外部loaderへ依存しない代わりに、CRP間でruntime textureを完全共有できない。

当面は次の順序とする。

1. Source atlasとUV規約を全道路で共有する。
2. `optLevel = 0`で単独動作する道路生成・インポートを完成させる。
3. 道路数と実測memory・CRP容量から重複コストを確認する。
4. コストが問題になった場合だけ、本プロジェクト用のshared material loaderと`optLevel = 1`相当を追加する。

完全共有を最初から実装するとloader、asset保存、配布依存、未導入時fallbackの開発が先に必要になるため、生成器の完成を遅らせる。Source共有とruntime共有を別問題として扱う。

## 実装優先順位

1. Parameter JSONから2・3・4車線の横断面とlane metadataを生成する。
2. `road_surface` atlas規約、marking band、UV生成を実装する。
3. 路側線なし・solid・dashed・doubleのvariantを同じatlasで検証する。
4. Edge profileとして`NONE`、`FLAT_GUTTER`、`CURB_GUTTER`、`OPEN_DITCH`を実装する。
5. Node、LOD、全モードのFBX/XMLを完成させる。
6. ゲーム内で見た目、bend、junction、memory、draw callを測定する。
7. 測定結果が必要性を示した場合だけruntime texture共有を実装する。

## ゲームへインポート可能と判断する完了条件

1. Blender previewで、共通横断面と全5モードを持つ連続した単一segment/nodeメッシュを生成できる。
2. Lane JSONを読み書きしても、順序とlaneごとのmetadataが欠落しない。
3. UV、texture/material key、LOD FBXを生成し、検証できる。
4. Segment/nodeのselector variantを明示的なflag付きで出力できる。
5. 完全な`RoadAssetInfo` XMLを生成し、ローカルビルドしたRoadImporterを使用してCS1へ読み込める。
6. インポートした道路について、直線、カーブ、junction、end、upgrade、左側通行・invert挙動、使用する全モードをゲーム内で検証できる。

3～6が完了するまで、addonの出力はジオメトリ・仕様previewであり、完成したCS1アセットパッケージとは扱わない。

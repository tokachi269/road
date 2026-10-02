# 実装詳細

> 文書種別: 詳細資料

この文書は、現行実装のgeometry、UV、Runtime連携に関する詳細を保存する。
責務と依存方向の入口は[現在の構成と責務](../architecture.md)を参照する。
ここに記載した未検証事項は、実装済みの契約とはみなさない。

この文書は現在の実装で実在する責務境界だけを定める。試作中のschemaや未実装のmesh構成を、lintによって先に確定しない。

## State ownership

現在のBlender prototypeでは、順序付きlane、共有断面値、mode固有構造値、marking設定からpreviewを生成している。

- Laneはstable ID、順序、幅、方向、CS1 lane metadataを所有する。
- Blenderの断面表は、network lane各行に加えて左右共通の歩道surface幅と導出された路肩幅を`not lane`行として表示する。通常laneは各行で種別・幅・向き・乗り物と実効速度を表示する。速度、停止offset、接続可否は選択laneの詳細でsourceを確認し、明示overrideの追加またはResetを行う。歩道lane幅はsurface幅から合計0.50mの余白を引いて導出し、独立入力にしない。歩道の`Both/Pedestrian/None`と通常の高さも導出する。
- 歩道間幅、歩道幅、分離帯profile・全幅・路面からの高さは共有断面値が所有する。路肩幅は`(歩道間幅 - 車道lane合計 - 分離帯幅) / 2`から導出し、負になる場合は0として明示寸法を縮めず、超過量をBlenderで警告する。残幅を駐車に使う設定では、指定幅が左右とも収まる場合だけ実際のParking laneを2本導出し、片側だけは作らない。通常の車道高低差は道路単位の真偽値から`domain.py`の固定値0.30mを導出し、個別laneやmodeでは編集しない。
- Nodeの角を道路幅とは独立して広げる必須値は`NetInfo.m_minCornerOffset`として保持し、全modeへ同じ値を適用する。`m_halfWidth`は道路全幅の半分であり、node寸法の代替として倍化しない。
- Road colorは道路定義の`styles.surface.road_color`が所有し、全modeの共通surface Materialへ同じ値を適用する。mode固有値やtexture mask側へ重複保持しない。
- 道路線の編集単位は3規則（路側線の有無、同方向lane間の白線種別、対向中央線の色・線種）であり、個別boundaryごとの入力は持たない。道路定義が既定値を所有し、CS1配置時panelが同じ道路の次回配置値を所有する。各boundaryの有効状態とstyle IDはroleから導出する。生成済みIMT線はユーザ編集を上書きしない。
- Blender PropertyGroupは編集adapterであり、別の意味を決めない。
- Sceneは道路PropertyGroupの一覧とactive indexを保持する。各項目はlane、断面、marking、mode、Runtime入力を一式保持し、選択中の1件だけを編集・preview生成する。追加、複製、削除はこの一覧を操作する。JSONはactive roadの置換または新規項目としてimportでき、active roadまたは全項目をexportできる。spec directoryやTSVを定期走査せず、catalogとの同期状態をBlender側へ重複保持しない。
- Sceneは共有profile一覧も保持する。laneの速度、停止offset、接続可否のglobal既定値は`domain.py`で正確な`(lane_type, vehicle_type)`から解決する。現データで定義するのは`Vehicle/Car = 1.0/0.0/true`と`Pedestrian/None = 0.1/0.0/true`だけで、未知tupleへ値を推測しない。現行profile schema v1との互換経路ではglobal既定値、profile、lane明示overrideの順に解決するが、これを最終schemaとはみなさない。IMT外観はprofileが所有する。道路JSON v4は`profile_id`とlaneの`overrides`だけを保存し、profile本体は`profiles.json`へ保存する。全道路exportは同じdirectoryへprofile libraryも出すが、ファイル監視や自動同期はしない。migration、import、duplicate、exportは同じ正規化規則を使い、継承値と同じと証明できるoverrideだけを除去し、異値と未知tupleの明示値は保全する。標準と異なるIMT外観は専用profileへ分離する。
- RoadImporter XMLはCS1向けcompiled outputであり、編集正本にしない。

生成済みpreviewがある場合、断面・線・mode形状のUI変更は選択中modeへ最大0.15秒単位で反映する。スライダー操作が約0.60秒止まった後、他の生成済みmodeを一度だけ追従させる。未生成modeの作成とRuntime exportはこのlive preview更新では行わない。

Blender previewのMaterialはmodeや道路名から生成しない。`surface / structure / tunnel`の共通Material datablockをすべのmodeと道路種別で再利用する。Markingはsurface内のface bandであり別Materialにしない。Slopeの非surface部はtunnelを使う。GroundとElevated等のCS1 shader差はMaterialの所有ではなくOutput Entry Planの描画契約であり、共通texture setを参照することと分けて扱う。

現在のschema v4は`lanes`と`layout.strips`へ幅を重複して保存しているため、最終契約ではない。どちらを正本にするかは未決定であり、比較評価は`docs/design-decisions.md`に置く。歩道間幅は`shared_geometry.between_sidewalks_width`を編集正本とし、路肩stripはそこからのcompiled topologyとする。分離帯は明示要求により`shared_geometry.median`を編集正本、`layout.strip-median`をcompiled topologyとして追加した。同じ幅を持つためimport時に一致を検証し、不一致を黙って採用しない。ほかの新機能を既存の重複へ追加する判断には流用しない。

## Derived topology

現在のprototypeでは、stripとboundary topologyを順序付きlaneと共有断面値から導出している。

- 車道strip IDはstable lane IDから導出する。
- Lane間boundary IDは左右のstable lane IDから導出する。
- 分離帯は最初の対向方向boundaryへ挿入し、直接のlane dividerを左右の`boundary-median-left/right`へ置換する。対向方向boundaryがない道路では分離帯を生成せずvalidation errorにする。
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

Elevatedの左右端部は、Blender内の右側基準Mesh Object 1つで固定断面fasciaを置換できる。Object原点のX/Zを右路面外角anchorとし、右はそのまま、左はX反転と面頂点順の反転を行って配置する。原点は配置基準であり、Meshの頂点や辺を必ず通す必要はなく、断面のlocal X/Z offsetは保持する。Z<0だけを床版厚へ追従させ、最下面に長手辺が2本ある直方体でも、道路中心側の辺を床版下面の端へweldする。長手方向のsliceはカーブ変形用に必要な通常面だけに入れる。路面高さで全面を一律分割せず、床版内部に埋まる道路中心側面の下部だけを切り取る。また64m両端の入力end capは接続先と重なるため出力しない。`CS1_NO_SPLIT` vertex groupの頂点に触れる面は柵等の剛体部として分割しない。指定時も出力はmodeごとのsegment/node各1 objectのままである。入力Object参照はBlender adapterの編集状態であり、未確定のschema v3へ追加しない。

分離帯は全mode共通のsurface断面である。生成curbを選ぶ場合、左右curb上面と中央上面は別faceにし、中央上面をX=0で分割しない。分離帯下のroad faceは作らず、左右立面の路面側角を2mm下げ、長手端を2mm延長して同一面の重なりによるz-fightingを避ける。生成curbを選ばない場合はBlenderの基準Mesh Object 1つを設定幅・設定高へfitし、segment 20/node 8 sliceへ切る。64m両端のend capは出力せず、`CS1_NO_SPLIT`頂点に触れる面は切らない。基準Meshがない状態はvalidation errorであり、暗黙の箱へ置換しない。

## UV ownership

現在の実装は、接続された一体meshを保ったまま、face semanticごとに共通atlasのregionへUVを割り当てる。通常の道路端は歩道上面1枚、curb壁、既存の路面faceで構成し、`curb.upper`や`curb.lower`のtexture素材を理由に細い面帯を追加しない。歩道上面のUV幅は`sidewalk.default + curb.upper`、壁に接する最外側路面のUV幅は`curb.lower + 接しているasphalt region`とし、共有辺のUをそれぞれ`curb.wall`の上端・下端へ一致させる。合成幅の正本は`textures/dimensions.json`のregion `width_px`であり、Blenderの形状幅へスケールして使用する。生成curb付き分離帯だけは、明示仕様によりcurb上面と中央上面を別faceにする。左右は同じregionを反転利用する。長手方向のslice数は増やさない。

Network materialの長手方向scaleは全geometry familyで`0.5`を基準とし、標準時の4反復相当を2反復相当へ減らす。Texture制作上の1周期は16 m、2048 pxの縦幅は2周期の32 mとして扱う。縦横とも64 px/mを基準とする。日本の標準的な6 m塗装を維持する破線では残り10 mを空白にする。白線の周期だけを理由に別mesh、別material、別segment entryを作らない。縦方向の情報量が不足した場合は、同じmeshのUVに使うV範囲を広げて調整し、面分割やdraw call追加では対応しない。Runtime preview bundleは`main_texture_scale = [1, 0.5]`を明示し、RuntimeHostがUnity Materialへ適用する。最終CRPで同値を復元する保存・load契約はOutput Entry Plan/RoadImporter側の未実装事項として分離する。

制作atlasの寸法・固定slot・正規化U座標のDecision ownerは`textures/dimensions.json`とする。PSDとGenerator出力はtexture制作物であり、座標の正本ではない。個別素材は`textures/base_layers`へ置き、基準配置PNGはmanifestから再生成する。2048 pxを制作解像度、1024 pxを縮小候補とし、全region幅は1024側でも整数pxになるよう偶数にする。通常regionの座標は偶数とするが、32 px slot内へ26 pxのline bandを中央配置する場合だけ左右3 pxとなるため開始座標は奇数を許容する。縮小時はUを丸めず、2048と同じ正規化座標（1024上では半画素境界）を維持する。slotは2048側32 px grid、通常の外周paddingは32 pxとし、paddingのedge extrusionとmipmapは個別layerではなく最終atlas出力時に生成する。`surface`は歩道、curb profile、路肩、路面を0..1536へ置き、線用32 px slot群を1536..2048へ置く。curbを路面系から離れた専用bankへ分離しない。lineの26 px bandは内部に透明域を持つため、種類追加は空slotを使用し、容量を超える場合は既存UVを移動せず、新しい共通texture setを追加する。

道路端用profileは`edge.sidewalk 224..384 / curb.upper 384..390 / curb.wall 390..400 / curb.lower 400..416 / edge.asphalt 416..608 px`として内部paddingなしで連続配置する。歩道上面は`224..390`、壁は`390..400`を使う。路肩・車線の指定幅は側溝上面を含むため、0.5 m路肩は`curb.lower 0.25 m + asphalt 0.25 m`の`400..432`、3.0 m車線へ直接接続する場合は`curb.lower 0.25 m + asphalt 2.75 m`の`400..592`を使う。これらの固定値は`uv_profiles`へ明示し、個別region幅からBlender側で暗黙に再計算しない。これはtexture制作上の区分であり、通常道路へ上面・下面の専用faceを追加する規定ではない。壁面10 pxは将来の見かけ高0.15 mを64 px/mで制作する値であり、現行preview geometryの0.30 m高とは分けて`dimensions.json`へ記録する。UV値は手入力せず、regionの`x_px / atlas_width_px`から生成する。`tools/validate_texture_layout.py`はPNG寸法、slot重複、profile包含、2048から1024への整数幅縮小、正規化UVを検査する。固定pixel値はunit testにも独立に列挙し、manifestと実装が同じ誤値へ同時にずれても検出する。Blenderは同じmanifestからregionを解決し、PSD座標をBlender側へ複製しない。

PhotoshopのGenerator Plugins / Image Assetsが出す`textures/road-assets/<family>_<map>.png`をBlender previewとRuntime previewの画像入力にするが、座標の正本にはしない。familyは`surface / structure / tunnel`、mapは`d / a / p / r / n / s`である。PSD自体はRuntimeへ渡さない。出力directory、family、map ID、ファイル名、Runtime packing、UV座標のDecision ownerは`textures/dimensions.json`である。`d`だけを必須とし、存在する任意mapだけを取り込む。Runtimeでは`d`を`_MainTex`へ割り当て、`a / p / r`を`_APRMap`へ、`n / s`を`_XYSMap`へchannel packする。`_APRMap = (1-a, 1-p, r)`、`_XYSMap = (n.r, n.g, 1-s)`とし、欠損channelの既定値はAPRが`(0, 1, 0)`、XYSが`(0.5, 0.5, 1)`である。Blender previewは`d / a / n / s`をPrincipled BSDFへ接続する。`p / r`は画像nodeとして読み込むが、CS1 theme側のPavement/Road textureが入力にないため、見た目を推測して合成しない。線の種類は同manifestの32 px slot間隔で配置するが、0.4 mの線faceのUはslot内の26 px content regionへ割り当てる。他のsurface faceも各semantic regionへ割り当て、座標をBlender側へ重複定義しない。生成curb付き分離帯もcurb壁・上面・中央面へ同じregionを再利用する。任意指定meshのUVは入力meshのものを保持する。

32 pxの`slot_width_px`は種類追加・padding・縮小整合のための配置単位であり、faceへ割り当てるUV幅ではない。生成コードは`slot_width_px`を参照せず、必ず各`region`の`x_px / width_px`を使う。生成faceはUVとregion IDを同時に持ち、UV未指定を汎用0～1投影で補完しない。これにより、管理枠32 pxを白線の実内容26 pxと取り違える経路をなくす。

`structure`と`tunnel`も同じmanifestを使う。床版下面、Elevated/Bridge fascia、主桁側面・下面は`structure_base_2048.png`、トンネル・Entranceの壁と天井／下面は`tunnel_base_2048.png`のsemantic regionへ割り当てる。生成形状だけを自動割当の対象にし、Elevated端部や分離帯として指定された任意meshのUVは書き換えない。

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

## Runtime preview boundary

Runtime previewはAsset Editor/CRP生成とは別経路である。

IMT previewの線・停止線・ゼブラは、Blenderの道路定義にある共通appearance（白／黄の色、Texture、Cracks、Voids）を使う。Cracks等を種類ごとに重複保持せず、線種は形状と色差だけを持つ。対向するvehicle lane間だけを任意で黄色にでき、同方向lane間と路側線は白を使う。ゼブラ幅・縞／空白・内側offsetと停止線幅も同じ設定箇所からRuntime bundleへ出力する。IMT 1.15の公開APIはゼブラ専用の外側端点offsetを公開していないため、共有入口点を動かして路側線や停止線までずらす実装はしない。

線・ゼブラ・停止線のstyle、再計算、ユーザー編集後の保存値はIMTが所有する。RuntimeHostはmissing時だけ初期値を作り、既存styleを道路データ再適用で削除・再作成しない。IMTの個別削除を監視して即時復元する処理も持たず、同じRuntimeセッションでは一度初期化した位置へ自動再作成しない。これはIMTのDeleteをRuntimeHostのresetへ読み替えないためである。対象道路をIMTで編集している場合だけ、IMT自身のheaderへ`Restore road defaults`を表示する。この明示操作は選択中nodeまたはsegmentの全markingを一度clearし、現在の接続とTM:PE条件を評価して、初期生成と同じ関数から既定値を再生成する。IMT本来の`Clear`と`Reset offset`の意味は変更しない。hot Runtime差替えをまたぐ個別削除状態の保持は未検証である。

IMT 1.15の公開APIはゼブラ専用の外側端点offsetを公開していないため、共有入口点を動かして路側線や停止線までずらさない。対象道路のwall位置へゼブラを合わせる処理は、IMT 1.15にversion gateしたHarmony hookで`MarkingCrosswalk.GetTrajectory`の境界生成を置換する。左右wallはIMTが計算した`Entrance.FirstPointSide / LastPointSide`を使い、crosswalk lineと左右border trajectoryを同じ外側位置へ合わせる。これにより縞の基準線だけを伸ばして元幅のcontourで再切断する状態を避け、LOD0 / LOD1のどちらも同じ境界から生成する。平行縞の本数は、Move It中の角度変換後のtrajectory長を`floor`するのではなく、`Entrance.RoadHalfWidth * 2`の正本幅と縞幅＋間隔から決める。比率が整数の浮動小数誤差内ならその整数を使い、7 m / 1 m周期のような境界で6本と7本を往復させない。gap grouping、非平行縞、またはIMTで左右borderのどちらかを明示指定したcrosswalkはユーザ編集を優先し、native IMT生成を使う。その後の破線分割とdecal生成はIMTへ委ねる。完成後の各decal polygonを伸ばす旧方式は、各縞の重複と全面塗りを生むため使用しない。hookは対象道路のcrosswalk再計算時だけ実行し、frame pollや全node走査を追加しない。

2本接続nodeでは対応する道路境界の線だけを両segment間へ接続し、ゼブラと停止線は作らない。IMTのnode内trajectoryは直線になるため、破線を使うとbend上で横向きの短い線片に見える。したがって2本接続nodeのconnectorは外側・内部境界ともsolidとし、中央線色の設定だけを維持する。3本以上のnodeでは、対象道路に歩行者laneがあり、かつTM:PEの横断許可（TM:PEがない場合はvanilla crossing flag）が有効な入口だけへゼブラを作る。停止線は流入vehicle laneがあり、信号、TM:PEのStop指定、または「詰まった交差点への進入」が不許可のいずれかに該当する入口へ作る。TM:PEがない場合は従来の既定表示を保つため、詰まった交差点への進入を不許可として扱う。TM:PEの変更通知は通知対象nodeまたはsegment両端だけをsimulation actionへまとめ、接続slot最大8本から対象`NetInfo`を判定する。通知ごとの全segment走査や定期pollは行わない。

IMT previewの新規道路検出も1秒pollでは行わない。CS1本体の`NetManager.CreateSegment`は通常overloadから`TreeInfo`付きoverloadへ委譲するため、後者1か所の成功postfixだけで作成segment IDと`NetInfo`を受け取る。hookは対象道路の配置時3規則をその場でsnapshotし、IMT APIを呼ばずbatchへ積む。`CreateSegment`完了後のSimulation actionはそのsegmentと両端nodeだけを評価し、node側も接続slot最大8本だけを調べる。既設segmentの全走査は行わない。hot Runtimeの新旧版が一時的に共存しても旧版の停止が新版patchを外さないよう、Harmony owner IDはassembly moduleごとに分ける。これは処理範囲の上限を定める設計であり、実ゲーム上の処理時間は未計測である。

```text
TSV catalog ──compile──> catalog.json
                              │
Blender generated/editable mesh
        └──baked mesh bundle──┼──> preview directory
                              │
stable Loader ──loads──> versioned Runtime DLL
                              │
                              └──> NetInfo / PropInfo in-place update
```

Runtimeは断面値からmeshを生成しない。Blenderで確定した頂点、法線、UV、三角形、material区分をUnity `Mesh`へ復元するだけである。TSVのlane値は`NetInfo.Lane`、Prop配置、構造signature、試験区画の再生成判断に使う。

Runtime exportは存在するGenerator PNGをpreview directoryの`textures`へatomic copyし、その内容hashをmanifestの`texture_revision`へ入れる。RuntimeHostは画像pathごとにsource Texture2Dを1つだけ共有し、source path集合ごとにpacked Texture2Dを共有する。`texture_revision`だけが変わった場合はsourceを同じTexture2Dへ再読込し、APR/XYSを同じpacked Texture2Dへ再構築するため、道路meshや既設segmentを作り直さない。

ロード済みCS1 Prefabの確認は、preview directoryの`inspect.request.json`から対象名と区分を明示する読み取り専用経路で行う。これは道路定義の正本やcompiled outputではない。全Prefab dumpは提供せず、検索・詳細とも最大20件、request 16 KiB、response 64 KiBに制限する。取得区分は`summary / lanes / lane_props / segments / nodes`であり、Runtimeは指定外の区分を再帰的にserializeしない。

Blender object内のmaterial slotは、export時にCS1のsegment/node entryへ展開する。したがってBlenderのobject分割とCS1 entry数は1対1ではない。selectorは現在templateの先頭entryから継承しており、複数selector contractの表現方法は未確定である。

Loaderはゲームから読み込まれ続ける最小assemblyで、`runtime.current`が指すversioned Runtime DLLを約1秒ごとに確認する。切替時は新Runtimeの`Start`が成功してから旧Runtimeの`Stop`を呼ぶ。Mono AppDomainから旧assemblyをunloadするものではなく、繰り返し差し替えるとassembly分のメモリはプロセス終了まで残る。

Runtimeは同名のロード済み`NetInfo` / `PropInfo`があればそのobjectを更新し、新規の場合だけtemplate prefabをcloneして登録する。表示変更では既設segmentを削除しない。lane構造signatureが変わったときに作り直すのはHostが所有する試験区画だけであり、ユーザーが敷設した道路は削除しない。既設道路に対するlane数変更のゲーム内安全性は未検証である。

Adaptive Roadsを前提に条件値を`namespace + value_json`で保持する。現在Runtimeが適用するnamespaceは`vanilla.lane`、`vanilla.start_node`、`vanilla.end_node`だけである。Adaptive Roads固有namespaceは失わず警告するが、reflection adapterによる反映は未実装・未検証である。

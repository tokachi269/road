# Road generator architecture

この文書は現在の実装で実在する責務境界だけを定める。試作中のschemaや未実装のmesh構成を、lintによって先に確定しない。

## State ownership

現在のBlender prototypeでは、順序付きlane、共有断面値、mode固有構造値、marking設定からpreviewを生成している。

- Laneはstable ID、順序、幅、方向、CS1 lane metadataを所有する。
- Blenderの断面表は、network lane各行に加えて左右共通の歩道surface幅と路肩幅を`not lane`行として表示する。通常laneは各行で種別・幅・向き・乗り物・速度を直接編集する。歩道lane幅はsurface幅から合計0.50mの余白を引いて導出し、独立入力にしない。歩道の`Both/Pedestrian/None`と通常の高さも導出し、`stop_offset=0`と`allow_connect=true`は通常時の既定値として内部に保持する。いずれも選択行の詳細フォームには出さない。
- 歩道幅、路肩幅、分離帯の有無・全幅・路面からの高さは共有断面値が所有する。通常の車道高低差は道路単位の真偽値から`domain.py`の固定値0.30mを導出し、個別laneやmodeでは編集しない。
- 現在の線設定は、導出されたboundary IDに対応付けている。
- Blender PropertyGroupは編集adapterであり、別の意味を決めない。
- RoadImporter XMLはCS1向けcompiled outputであり、編集正本にしない。

生成済みpreviewがある場合、断面・線・mode形状のUI変更は選択中modeへ最大0.15秒単位で反映する。スライダー操作が約0.60秒止まった後、他の生成済みmodeを一度だけ追従させる。未生成modeの作成とRuntime exportはこのlive preview更新では行わない。

Blender previewのMaterialはmodeや道路名から生成しない。`surface / structure / tunnel`の共通Material datablockをすべのmodeと道路種別で再利用する。Markingはsurface内のface bandであり別Materialにしない。Slopeの非surface部はtunnelを使う。GroundとElevated等のCS1 shader差はMaterialの所有ではなくOutput Entry Planの描画契約であり、共通texture setを参照することと分けて扱う。

現在のschema v3は`lanes`と`layout.strips`へ幅と接続を重複して保存しているため、最終契約ではない。どちらを正本にするかは未決定であり、比較評価は`docs/design-decisions.md`に置く。分離帯だけは明示要求により`shared_geometry.median`を編集正本、`layout.strip-median`をcompiled topologyとして追加した。同じ幅を持つためimport時に一致を検証し、不一致を黙って採用しない。ほかの新機能を既存の重複へ追加する判断には流用しない。

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

制作atlasの寸法・固定slot・正規化U座標のDecision ownerは`textures/dimensions.json`とする。個別素材は`textures/base_layers`へ置き、完成配置PNGはmanifestから再生成する。2048 pxを制作解像度、1024 pxを縮小候補とし、1024側の1 pxを2048側の2 pxとして全regionを偶数幅・偶数座標にする。slotは2048側32 px grid、通常の外周paddingは32 pxとし、paddingのedge extrusionとmipmapは個別layerではなく最終atlas出力時に生成する。`surface`は既存PSDの作業配置を基準に、歩道、curb profile、路肩、路面を0..1536へ置き、線用32 px slot群を1536..2048へ置く。curbを路面系から離れた専用bankへ分離しない。lineの26 px bandは内部に透明域を持つため、種類追加は空slotを使用し、容量を超える場合は既存UVを移動せず、新しい共通texture setを追加する。

curb profileのtexture素材は歩道側上面6 px、壁面10 px、車道側下面16 pxを内部paddingなしで連続配置し、その32 px groupの外側だけをpaddingする。ただし、これはtexture制作上の区分であり、通常道路へ上面・下面の専用faceを追加する規定ではない。壁面10 pxは将来の見かけ高0.15 mを64 px/mで制作する値であり、現行preview geometryの0.30 m高とは分けて`dimensions.json`へ記録する。UV値は手入力せず、regionの`x_px / atlas_width_px`から生成する。`tools/validate_texture_layout.py`はPNG寸法、slot重複、2048から1024への整数縮小、正規化UVを検査する。Blenderは同じmanifestからregionを解決し、PSD座標をBlender側へ複製しない。

Blenderの生成道路が共有する`CS1 Road Shared Surface` Materialは、`textures/road.psd`をImage Textureとして直接参照する。PSDの更新はDevelopmentパネルから再読み込みできる。線の種類は`textures/dimensions.json`の32 px slot間隔で配置するが、0.4 mの線faceのUはslot内の26 px content regionへ割り当てる。他のsurface faceも各semantic regionへ割り当て、座標をBlender側へ重複定義しない。生成curb付き分離帯もcurb壁・上面・中央面へ同じregionを再利用する。任意指定meshのUVは入力meshのものを保持する。

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

Blender object内のmaterial slotは、export時にCS1のsegment/node entryへ展開する。したがってBlenderのobject分割とCS1 entry数は1対1ではない。selectorは現在templateの先頭entryから継承しており、複数selector contractの表現方法は未確定である。

Loaderはゲームから読み込まれ続ける最小assemblyで、`runtime.current`が指すversioned Runtime DLLを約1秒ごとに確認する。切替時は新Runtimeの`Start`が成功してから旧Runtimeの`Stop`を呼ぶ。Mono AppDomainから旧assemblyをunloadするものではなく、繰り返し差し替えるとassembly分のメモリはプロセス終了まで残る。

Runtimeは同名のロード済み`NetInfo` / `PropInfo`があればそのobjectを更新し、新規の場合だけtemplate prefabをcloneして登録する。表示変更では既設segmentを削除しない。lane構造signatureが変わったときに作り直すのはHostが所有する試験区画だけであり、ユーザーが敷設した道路は削除しない。既設道路に対するlane数変更のゲーム内安全性は未検証である。

Adaptive Roadsを前提に条件値を`namespace + value_json`で保持する。現在Runtimeが適用するnamespaceは`vanilla.lane`、`vanilla.start_node`、`vanilla.end_node`だけである。Adaptive Roads固有namespaceは失わず警告するが、reflection adapterによる反映は未実装・未検証である。

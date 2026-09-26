# Road generator architecture

この文書は現在の実装で実在する責務境界だけを定める。試作中のschemaや未実装のmesh構成を、lintによって先に確定しない。

## State ownership

現在のBlender prototypeでは、順序付きlane、共有断面値、mode固有構造値、marking設定からpreviewを生成している。

- Laneはstable ID、順序、幅、方向、CS1 lane metadataを所有する。
- Blenderの断面表は、network lane各行に加えて左右共通の歩道surface幅と路肩幅を`not lane`行として表示する。通常laneは各行で種別・幅・向き・乗り物・速度を直接編集する。歩道lane幅はsurface幅から合計0.50mの余白を引いて導出し、独立入力にしない。歩道の`Both/Pedestrian/None`と通常の高さも導出し、`stop_offset=0`と`allow_connect=true`は通常時の既定値として内部に保持する。いずれも選択行の詳細フォームには出さない。
- 歩道幅、路肩幅は共有断面値が所有する。通常の車道高低差は道路単位の真偽値から`domain.py`の固定値0.30mを導出し、個別laneやmodeでは編集しない。
- 現在の線設定は、導出されたboundary IDに対応付けている。
- Blender PropertyGroupは編集adapterであり、別の意味を決めない。
- RoadImporter XMLはCS1向けcompiled outputであり、編集正本にしない。

生成済みpreviewがある場合、断面・線・mode形状のUI変更は選択中modeへ最大0.15秒単位で反映する。スライダー操作が約0.60秒止まった後、他の生成済みmodeを一度だけ追従させる。未生成modeの作成とRuntime exportはこのlive preview更新では行わない。

Blender previewのMaterialはmodeや道路名から生成しない。`surface / structure / tunnel`の共通Material datablockをすべのmodeと道路種別で再利用する。Markingはsurface内のface bandであり別Materialにしない。Slopeの非surface部はtunnelを使う。GroundとElevated等のCS1 shader差はMaterialの所有ではなくOutput Entry Planの描画契約であり、共通texture setを参照することと分けて扱う。

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

Elevatedの左右端部は、Blender内の右側基準Mesh Object 1つで固定断面fasciaを置換できる。Object原点のX/Zを右路面外角anchorとし、右はそのまま、左はX反転と面頂点順の反転を行って配置する。原点は配置基準であり、Meshの頂点や辺を必ず通す必要はなく、断面のlocal X/Z offsetは保持する。Z<0だけを床版厚へ追従させ、最下面に長手辺が2本ある直方体でも、道路中心側の辺を床版下面の端へweldする。長手方向のsliceはカーブ変形用に必要な通常面だけに入れる。路面高さで全面を一律分割せず、床版内部に埋まる道路中心側面の下部だけを切り取る。また64m両端の入力end capは接続先と重なるため出力しない。`CS1_NO_SPLIT` vertex groupの頂点に触れる面は柵等の剛体部として分割しない。指定時も出力はmodeごとのsegment/node各1 objectのままである。入力Object参照はBlender adapterの編集状態であり、未確定のschema v3へ追加しない。

## UV ownership

現在の実装は、明示要求に従い歩道上面、curb立面、路面を連続した断面展開にし、線bandはalpha余白を含む独立0～1領域を使う。目標はcurb壁の接続位置を両側で一致させ、壁面用の幅を確保した上で領域ごとのscaleを調整することであり、全領域を同じscaleへ固定することではない。

Network materialの長手方向scaleは全geometry familyで`0.5`を基準とし、標準時の4反復相当を2反復相当へ減らす。Texture制作上の1周期は16 m、1024 pxの縦幅は2周期の32 mとして扱う。日本の標準的な6 m塗装を維持する破線では残り10 mを空白にする。白線の周期だけを理由に別mesh、別material、別segment entryを作らない。縦方向の情報量が不足した場合は、同じmeshのUVに使うV範囲を広げて調整し、面分割やdraw call追加では対応しない。Runtime preview bundleは`main_texture_scale = [1, 0.5]`を明示し、RuntimeHostがUnity Materialへ適用する。最終CRPで同値を復元する保存・load契約はOutput Entry Plan/RoadImporter側の未実装事項として分離する。

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

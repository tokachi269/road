# 連携会話の設計判断

この文書は確定仕様ではなく、連携された会話の主張を現在の要求、`wire`のowner/derived分離、RoadImporterとCSURの実装、現行prototypeに照らして判定したreview記録である。ここに書いた候補をarchitecture lintは強制しない。

## 判定の要点

連携会話は、個別道路を手作業せず断面から量産する方向、高架主桁を横断面として生成する方向、Adaptive Networksを根幹にしない方向は妥当である。

一方、次の説明はそのまま採用できない。

- Laneをmesh上の中心概念から外す、という表現は強すぎる。Laneは交通、接続、transitionのidentityとして正本に必要である。Mesh face列はlaneから導出してよい。
- `surface / structure / tunnel`という意味分類だけでmesh entryを決めてはいけない。CS1 entryはselector flag、shader、textureと対になっている。
- 複数mesh entryを登録できることだけでは、全entryが同時描画される証明にならない。対応する`m_segments / m_nodes`とselector presetも生成する必要がある。
- 共通atlasを作ることと、複数CRP間でruntime textureを共有することは別問題である。
- 主桁のAOに関する指摘はbake方式の選択ではなく、1枚の面へ同じ部品を配列するとtexture境界が不自然になる問題である。配列部品ごとにUVをリセットするだけでは解決しない。

## 採用候補として推奨

### 1. 人が編集する入力を絞り、断面を導出する

任意のstrip graphをユーザーへ直接編集させない。順序付きlane、歩道・路肩・curb、median、道路端、線、mode構造値を入力し、横断face列とanchorを導出する。

ただしlane数だけの入力には戻さない。3車線、非対称lane、turn lane、交通方向、node接続を扱うため、順序付きlane自体は必要である。

### 2. Stable lane IDを今から持つ

「transitionを本格実装するときにIDを足す」は採用しない。Lane追加・削除transitionとnode接続が既に要求範囲だからである。

Boundary topologyをlane adjacencyから導出する方式は有力だが、線overrideをboundary IDで永続化する方式まで確定はしない。Lane挿入・削除時にどの設定を継承するかをscenarioで決める必要がある。

### 3. Cross-section extrusionとanchorを共通化する

Segmentは横断面を長手sliceへ押し出す。`roadLeft/right`、`deckLeft/right`、median edge等を一度導出し、guardrail、壁、桁のconsumerが幅を再計算しない。

ただしnode全体をsegmentと同じ押し出しだけで表現することはできない。Transition nodeには使えるが、junction、end、bend等は別topologyが必要である。

### 4. Mesh entryは出力契約で分ける

RoadImporterは`segmentMeshes / nodeMeshes`と`CSNetInfo.m_segments / m_nodes`を対応するindexで扱う。CSURもlane meshとstructure meshを分けるたび、同じselector preset等を持つsegment entryを追加している。

したがって分割基準は次の順にする。

1. Selector flagまたはconnect groupが違うか。
2. Shaderが違うか。
3. Texture setまたはUV規則が違うため分離が必要か。
4. それ以外は同じentry内の一体meshにできるか。

通常のElevated/Bridgeでは`surface`と`structure`の2 entryが有力である。Tunnel壁面は固定の第3 familyにせず、RoadBridge shaderを使うstructure entryへ含められるかを先に検証する。Tunnel Entranceは上下方向やselectorが異なるentryを必要とする可能性がある。

線は静的ならsurface内のface bandへ含める。Bike policy等で表示切替が必要になった線だけ、selector付き別entryを検討する。CSURが線を別entryにしているのは、この動的切替も理由に含まれる。

### 5. 主桁は横断面profileから生成する

ユーザーが指す主桁は道路幅方向に複数並び、道路方向へ連続する。短い部品を長手Arrayするのではなく、横断面profileを長手方向へ押し出す方針でよい。

Blender確認段階の断面は矩形に限定する。自由な幅・深さ入力は床版外へのはみ出しと根拠のない比率を許すため採用しない。国交省資料のJIS A 5373 PCコンポ橋標準（桁間隔2.6/3.2/3.8m）から床版幅に適合する構成を選び、確認用支間35mの桁高1.8/2.1/2.5mを使う。矩形幅はPC工学会資料の標準主桁断面にある下フランジ全幅0.70mを外形近似として使う。

床版と主桁は同じstructure entryへまとめる候補だが、必ず頂点共有するとは決めない。Manifoldな一体surfaceにする箇所だけweldし、hard normalが必要な角、材質境界、非接続部は分ける。重複内部faceは作らない。

橋脚は`BridgeAIProperties`のpillar prefab指定があるため、通常segmentへ焼き込まない。

Blender previewの初期本数は車道幅を約3.25 mで割った値を最寄りの偶数へ丸め、最低2本とする。通常の2車線で2本、4車線で4本になる目安であり、lane数そのものを本数へ直結させない。自動規則を最終仕様にはせず、必要なら後でrecipe値へ置き換える。

### 6. Variantは明示した出力集合だけ生成する

機能の直積を全件asset化しない。Generatorが組合せを扱えても、実際に出す道路はrecipeまたはbuild manifestで明示する。

ただしrecipe継承はまだ不要である。Presetは入力補助として展開し、保存後にpresetと展開結果を二重正本にしない。

### 7. Adaptive Networksを必須依存にしない

基本幅、lane、mesh、接続がAdaptive Networksなしで成立するようにする。将来使う場合も、追加壁面やprop等の任意表示に限定する。

## 現時点では採らない

### 1. schema v3を最終modelとして拡張する

現在はlane幅、strip幅、adjacencyが重複し得る。まずauthoritative inputとcompiled layoutを分ける必要があり、現在のv3へmedian、gutter、girder等を足し続けない。

### 2. Surface UVをsemanticごとに直ちに分断する

歩道上面、curb立面、路面をつなげて展開するという明示要求と衝突する。ここでいう連続とは、curb壁の両端位置を隣接面と一致させ、壁面分のUV幅を多めに確保し、各領域のscaleを調整して境界をずらさないことを指す。全断面を同じscaleで単純正規化する意味ではない。

幅が変わるとtexture scaleが変わる問題は残るが、調整規則の詳細は後で詰める。今はsemantic seamへ変更しない。

### 3. `surface / structure / tunnel`の3分類を固定schemaにする

これは整理用の呼称としては使えるが、CS1の実際の登録単位を決める情報が不足している。登録recordには最低でもgeometry、shader、texture set、segment/node selector presetが必要である。

### 4. 1 atlas setでruntime共有まで解決したとみなす

現在の独自版RoadImporterは`optLevel=0`で、各CRPへtextureを埋め込む。共通のsource atlasは制作とimport jobのunique texture数削減には役立つが、複数CRP間のruntime共有を保証しない。

本当にruntime共有するなら、外部loader依存、packaging単位、または別の共有方式が必要になる。これは独立道路という目的と配布負担を比較して別途決める。

## 実測後に決める

- 「一つのmesh」を1 CS1 entry単位と解釈してよいか。
- Surfaceとstructureの2 entryが同一selector条件で常に同時描画されるか。
- Nodeを何entryへ分け、intersection/transition/endのselectorをどう設定するか。
- Surface連続UVのscale変化が実ゲームで許容できるか。
- Atlasをsurface/structure/tunnel別にするか、画像setを共有できるか。
- Tunnel壁面をstructure entryへ含められるか。
- 主桁profile、本数、間隔をrecipe指定にするか、幅から導出するか。
- 床版・fascia・主桁をまたぐstructure UVと、反復textureの境界規則。
- Segment 20 slice、node 8 sliceを全道路で固定するか。
- `optLevel=0`のまま大量variantを配布した場合のCRP容量とmemory。

最小の縦切り検証は、2車線Ground、4車線Elevated、TunnelをそれぞれFBXとXMLへ出し、Asset Editorでstraight、curve、transition nodeを確認すること。その結果が出るまではmesh family数、atlas数、runtime共有方式をschemaへ固定しない。

ただしImporter検証は当面の作業順では後段に置く。先にBlenderで2・4車線のsurface、床版、fascia、主桁本数、断面、normal、連続UVを確認できるようにする。そのGeometry Planが固まってからOutput Entry PlanとRoadImporter XMLへ進む。

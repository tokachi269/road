# 配置時の断面variant

> 文書種別: 試作・調査
> 状態: domain、Blender mesh、compiled snapshotのRuntime読込みを接続済み。
> 内部Prefab登録と配置UIは試作として接続。非対称native node、transition、ロード復元は未完了。

今回の作業では、2026-10-14のユーザー指示により実ゲーム確認を完了条件から外す。
実装・独立検査・installまでを進め、ゲームでの未確認事項を結果に残す。
以下に記載する実ゲームgateは一般的な証明範囲の区別であり、今回の作業を待機させる条件ではない。

目的は、同じ交通構成のBase Roadから配置時に断面を選び、内部では
必要なNetInfoだけを使うこと。全組合せをToolbarへ追加することではない。
既設segmentが参照するNetInfoを、配置設定変更で書き換えない。
この文書は未実装項目を完了扱いにする契約ではない。

## 現在のownerと変更境界

| 項目 | 現在の実装 | 今回の境界 |
| --- | --- | --- |
| Base Road | `catalog/roads.tsv`、Blender roadのlane構成 | 14件とも異なる交通構成。4一通＋10対面を残す |
| 断面とlane位置 | `domain.py`とBlender adapter | 配置断面の判断は`resolve_placement_section`を単一ownerとする |
| mesh | Blenderの生成後mesh → Runtime bundle | `build_placement_mode`が解決済み断面を全5modeへ適用。通常build/exportは変更しない |
| NetInfo | `PrefabUpdater.ApplyRoad` | 現在は同名Prefabを更新する。新variant経路ではこの更新を流用しない |
| 5 mode | bundleのmode entriesと`LinkModes` | 同じ断面identityの5 modeをリンクする。Ground/高架を別Base Roadにしない |
| 線種 | `RoadPlacementMarkingController`の一時共通選択 | geometry identityに含めない。IMT個別編集は保全する |
| 交通分界 | 現在はlaneの方向と位置からIMT roleを導出 | 新resolverが実際のlane位置とboundaryを同時に出す |
| zoning | CS1 `RoadAI.CreateZoneBlocks` | `m_halfWidth`だけを偽装してgridを合わせない |
| map load | Loaderの`OnLevelLoaded`からRuntime開始 | 現在は復元に遅い。名前の決定論性だけでsave/load対応としない |

`schema v4`の`lanes/layout.strips`は増やさない。旧JSON、bundle、14道路を
この段階で移行・削除しない。車両以外の新しい交通構成を推測して対応しない。

## CS1コードで確認したwidth/grid

根拠はインストール済み`Assembly-CSharp.dll`の`RoadAI.CreateZoneBlocks`と
`ZoneBlock`のセル座標計算。ILSpyで調査した本体処理では、直線道路の
zone block横方向起点は`max(8, m_halfWidth) + 32`、セル間隔は8 m。
`distance`にも丸めたhalf widthを渡す。これは実ゲームの区画検証の代替ではない。

2026-10-13調査時の本体DLL SHA-256:
`df557994a03c93d8bb4a14f80b1fbcc0fb29d547658442445d078d069bcdd9fe`。

| 物理全幅 | zoningで使われる半幅 | 16 m道路からの横方向差 mod 8 | 注意 |
| --- | ---: | ---: | --- |
| 8 m | 8 m | 0 m | 物理端4 mとzoning起点8 mが異なる。通常familyに混ぜない |
| 16 m | 8 m | 0 m | 通常familyの基準 |
| 24 m | 12 m | 4 m | 8 m倍数でも標準道路と半cellずれる |
| 32 m | 16 m | 0 m | 基準と同じ横方向phase |
| 48 m | 24 m | 0 m | 基準と同じ横方向phase |

これは中心線が同じ8 m gridへ置かれた直線道路の横方向phaseの比較である。
異なる中心線位置、曲線、交差点、道路長、隣接blockの重複・切取り、建物区画を
保証しない。通常familyは16 m刻みで検証する。zoning専用offset hookはまだ導入しない。

## 断面resolverの仕様と試作preset

入力は順序付きlaneのID・幅・方向と`RoadPlacementOptions`。
出力は実幅、左右curb、左右歩道、roadside、中央帯、traffic boundary、
lane中心とboundary、road surface高さである。値は`domain.py`が所有する。

- 幅はNarrow/Standard/Wideの要求値から、断面が収まる次の許可幅まで拡張する。
  lane幅、中央帯、roadsideを縮めない。最大許可幅にも収まらなければ拒否する。
- presetの数値はdomainの試作値。Standard shoulderと最小歩道は現14道路の
  0.5 mと2.5 m、median standardは現編集値1 mを使う。
  Wide roadside/medianは試作値2 mで、ゲーム検証前の製品仕様とはしない。
- 対面道路はtraffic boundaryを0または左右1.5 mへ置く。1.5 mは現3 m laneの
  半幅であり、現在の1+2構成の境界offsetに対応する。線だけは動かさない。
- left方向groupとright方向groupを分界の両側へ配置する。lane pack全体を中心へ
  置いた結果を「Centered」と呼ばない。左右のlane数が違っても分界は指定位置にある。
- 指定roadside幅は車両lane外端からcurbまで。余剰幅は各側の外側歩道へ
  配分する。Noneでも隠れたroadsideを足さない。左右歩道幅は等しいとは限らない。
- 一通はlane packを中央へ置くが対向boundaryを持たない。offset dividerとmedianは
  無効な組合せとして拒否する。compiled menuから無効候補を除く。共通selectionに
  別Base Roadの無効値が残った場合は黙って置換せず、明示resetを使う。
- medianなしでは対向boundaryを中央線候補とする。medianありでは両端boundaryを
  出し、中央帯の中心へ白線を生成しない。
- D0/D30はphysical surfaceの高さ。CS1のlane vertical offsetとAIの高さ基準への
  placement adapterはcompiled値を使う。通常previewの旧lane高さ契約は変更しない。
  異なる高さのtransitionと実ゲームでの基準点の確認は未完了である。

`variant_identity(base_road_id)`は解決後の断面とlaneのstable ID/方向/幅を
canonical serializeしてhash化する。要求幅が違っても解決後が同じなら再利用できる。
線の色・線種・一時UI状態は含めない。これは生成/cache用identityである。
Runtime prefab名にはcompiled snapshot identityを使う。map保存形式はまだ未実装。

## Blender生成adapter

`build_placement_mode(scene, mode, section, rebuild=False)`はdomainの解決済み断面を
受け取り、laneの境界と左右curbから全5modeのsegment/nodeを生成する。
surface/UV、median、deck、指定高架端部、tunnelは既存の生成helperを使う。
通常の`CS1_ROAD` previewは置換せず、`CS1_PLACEMENT`配下に要求された断面だけを置く。
同じidentity/modeのmeshは再利用し、頂点の手編集を保持する。生成入力が変わった場合は
古いmeshを成功扱いせず明示rebuildを要求する。rebuildはstaging collectionの生成成功後に
当該placement collectionだけを置換し、エラー時には以前のmeshを残す。

新しく有効にするmedianは、Base Roadに明示的なMESH profileがある場合だけその指定meshを
fitして交通分界へ移動する。NONE/CURBから有効にする場合は生成curbを使う。この判断は
`domain.placement_median_uses_mesh`が所有し、NONEでfalseになっていた旧hidden switchを
「存在しない指定meshを使う」という要求として扱わない。

新adapterのD0 nodeはD0を保つ。旧previewのD0→D30 node profileは新variantへ流用しない。
標準curb上面/下面用の細いfaceや、style別geometryを追加しない。Runtime選択・zoning・lane軌道・
node変形をまだ適用した結果ではない。これらはBlender meshの検査で代替しない。

## 接続とtransition: 確認範囲

| 変更 | 本体側の確認 | 必要な次の検証・実装 |
| --- | --- | --- |
| 線種だけ | IMTが所有しNetInfoを変えない | 既存のevent適用を継続 |
| total width | nodeで隣接half width比から補間する経路がある | 異幅curb、直線性、zone blockの実ゲーム検証 |
| divider offset | lane positionはNetInfoごと、通常nodeは交通分界を入力しない | lane軌道、IMT point対応、curveの検証。線位置だけの対応は禁止 |
| median none/present | 面数・穴・boundaryが変わる | medianが別断面で終端・開始するmeshとlane接続を検証 |
| roadside/左右歩道 | `m_pavementWidth`は1値。nodeはこの値から左右SideScaleを作る | 非対称curbをそのまま安全と仮定しない。限定adapterまたは専用transitionの可否を確認 |
| D0/D30 | 1segmentのlane offsetは1値、nodeは他roadのD0/D30を認識しない | 両端状態を持つ一時transition segmentとlane軌道が必要 |

高さtransitionは`D30, D30→D0, D0`という一時segmentであり、常時有効な第三の
road levelではない。自動挿入できると未確認のままNetToolへhookしない。
最初は明示Transition操作を候補とし、完了した次のsegmentはtarget levelへ戻す。
Node Controllerのユーザー編集を修復対象にしない。異幅・offset・medianをすべて
専用transitionへ強制する方針もまだ採らない。

本体`NetAI.UpdateLanes`は、laneの`m_verticalOffset`を両端の位置へ同じ値で足して
lane curveを作る。したがってD30→D0 meshだけを生成しても車両軌道の一致は証明できない。
両端状態に追従するlane curveの適用と、nodeでの接続を別に検証する必要がある。
本体のlane Bezierのa/dはtraffic directionによらずsegmentのstart/endである。
描画の長手反転は`NetInfo.Segment.CheckFlags`が返す`turnAround`からMeshScale.x/yの
符号を変える。一方`RoadAI`は`CanAutoRenderInverted`をoverrideせず、基底のfalseを使う。
したがってlaneのdirectionやsegmentのInvertだけからmeshの始点高さを推測しない。
native adapterでは実際のentry条件と描画向きの整合を別に検査する。

## Runtime snapshotの受け渡し

`export_placement_choices(scene, options, output_root)`は明示された選択肢だけをcompileする。
断面、全lane geometry、全5modeのmeshは同じdomain解決結果から出す。左右歩行者laneの
幅と位置は各側の実歩道幅から導出する。車両lane offsetはmeshと同じcorner基準に対する
physical roadway高さ、歩行者lane offsetは0とする。旧断面のoffset値はコピーしない。

出力は`placement/<base-road-id>/index.json`とcontent-addressed snapshotであり、
通常の`manifest.json`へ道路を追加しない。要求widthが違っても実断面が同じなら同じsnapshotを
参照する。線種はsnapshotへ入れない。古いsnapshotは削除・書換えせず、参照表を最後にpublishする。
通常のBlender authoring JSON schemaやBase Road一覧には新しい道路定義を足さない。

`PlacementVariantLibrary`は参照表を検査し、選択されたsnapshotだけを読んでSHA-256を照合する。
位置や幅を再解決せず、traffic boundaryと実lane位置の一致、有限値、全5modeの完成を検査する。
cacheは検証済みJSONを保持し、consumerには独立したDTOを返す。後のconsumer編集で既設道路の
再現元を変えない。空の未compile選択肢を既定値へ寄せない。

各snapshotのmeshでは他materialの未使用頂点だけを除き、JSONはcompact出力とする。
intentional node seamのweldや頂点の近似は行わない。通常bundleのserializer結果は変更しない。

`PrefabUpdater.GetOrCreatePlacement`は内部登録用APIで、geometry identityとは別の
snapshot identityから決定論的な名前を作る。同じidentityを再利用し、新規5modeはToolbar非公開、
既設Prefabの上書きと既設rendererの全走査はしない。hot reload後の完成済みPrefabはidentity tagで
所有を照合する。左右歩道が異なることだけを理由に登録を拒否する暫定gateは、
下記のshader参照分離で撤去した。異断面nodeの専用補正はまだ未完了であり、
非対称接続を検証済みという意味ではない。CS1での成功登録・表示・ロード復元はまだ未検証。

## save/loadとUIの実装条件

### ARフラグとsegment個別設定の方針（2026-10-14）

ユーザー指示: Adaptive Roadsのフラグで切替可能な表示はその仕組みを使い、lane等は
必要に応じて個別設定する。共有道路の書換えによる街全体への変更と、重い更新処理を避ける。
以下は次の実装が守る境界であり、既存試作をAR経路へ接続済みという意味ではない。

- 個別設定の単位はsegment。laneはそのsegment所属のstable lane/indexへ対応する。
  配置UIの一時選択は新規segment作成の成功時だけ取り込み、同じBase Roadの既設segmentへ
  伝播させない。既設変更は対象を明示した操作だけとする。
- 共有NetInfo、`m_lanes`、半幅、Material、meshを個別操作で書き換えない。
  一時的な書換えから呼出し後に戻す方法も、共有値を他thread/consumerが読めるため採用しない。
  instance lane curve等の更新も、laneが対象segmentに所属することを確認してから行う。
- mesh selectorはARのsegment/node条件、個別のphysical lane値はcompiled断面を読む限定adapterを
  候補とする。lane位置・高さ・幅をRuntime側で別のpresetから再計算しない。
  segment固有値を読まないconsumerが残る場合は見た目だけで対応済みとしない。
  ARだけで対応できないNetInfo固有設定は個別adapterか最小限の内部NetInfo分離を比較する。
- 作成、明示変更、削除、upgrade、split/copy、lane再構築を更新契機とする。
  通常の操作では対象segmentのlane chainと両端nodeだけを扱い、nodeの接続調査は最大8本。
  node連続性のために隣接端点を再評価しても、隣接道路の個別設定は変更しない。
  毎秒監視、全segment探索、道路長に沿った無制限の更新伝播を追加しない。
- 選択変更ごとのmesh再生成、全variantの追加NetInfo登録、毎frameのJSON/texture読込みはしない。
  immutable compiled dataと事前解決した呼出し情報を再利用する。性能は更新件数、割当量、
  処理時間を測り、測定前に「軽い」と断定しない。
- 解放されたsegment IDの再利用で旧設定を引き継がない。copy/splitの設定引継ぎとupgrade時の
  topology対応は明示的に検査する。ARの保存に含まれる値を別の保存正本として二重管理しない。

独立検査の必要なcase: A/Bが同じBase NetInfoを使う状態でAだけ変更してBと共有値が不変、
接続nodeの再評価で隣接segmentの設定が不変、lane再構築後も同じ結果、削除→ID再利用で
旧値なし、copy/split/upgradeの継承・拒否、街のsegment数を増やしても操作対象件数が一定。
現時点でこれらのAR経路のbehavioral proofは未実装。

インストール済みAdaptive Roads 4.0.0の調査では、segment別のCustom flags/UserDataと
asset/mapの保存経路は存在する。`NetLaneExt`の描画は`LaneInfo.m_width`を読み、通常lane軌道は
本体の`NetAI.UpdateLanes`が共有`m_position/m_verticalOffset`から生成する。したがって
AR表示フラグの適用だけで個別physical lane値の全consumerを対応済みとは扱わない。
`NetworkExtensionManager.UpdateSegment/UpdateNode`にはlevel付き隣接伝播があるため、
adapterではその呼出し範囲も確認する。AR自体のmap保存時のbuffer走査と、こちらが追加する
通常操作時の全街走査を混同しない。

### CRP化後の提供方法の再確認（2026-10-14）

ユーザーとの確認により、Runtime previewだけのmap保存復元は実装を止めた。
先に、Asset EditorでCRP化した道路で配置時variantをどう提供するかを決める。
CRP化済みなら動的variantの復元が不要になる、と無条件には扱わない。

インストール済みCS1の`SaveAssetPanel.OnSave`、`AssetDataWrapper`、
`LoadingManager.LoadCustomContent`と、本リポジトリの`RoadAssetInfo`から確認した範囲:

- 標準の道路保存は編集中のBase NetInfoとElevated/Bridge/Slope/Tunnelを格納する。
  本試作のplacement index、通常variant、height transition一覧を自動で取り込む処理はない。
- `IAssetDataExtension.OnAssetSaved`が返す名前付きbyte列はCRPのUserAssetDataへ格納される。
  本体はcustom content読込み中に`OnAssetLoaded`へ渡し、その後に
  `EssentialScenesLoaded`を通知する。追加compiled dataをCRPへ持たせる入口は存在する。
  本試作にはこのasset extensionはまだない。読込み順を確認しただけで、追加NetInfo登録の
  成功や他loader MODとの互換性を証明したとは扱わない。
- 現在のplacement readerとUIはRuntime Host側にあり、通常CRP道路へ接続していない。
  Blenderの現行exportもRuntime bundle向けであり、最終FBX/XML/CRP経路は別途必要である。

候補はBase RoadのCRPへ検証済みcompiled variant dataを格納し、製品用の対応MODが
配置UIと内部NetInfo登録を受け持つ経路。preview.path、ローカルBlender出力、hot DLLへの
依存を製品経路へ持ち込まない。これは候補であり、配布形式を確定した扱いにしない。
断面判断のownerは引き続きdomainとBlenderで、ゲーム側は解決済み出力を消費する。

この候補でもNetInfoを使用時だけ動的登録するなら、map再読込み時に使用済みvariantを
Prefab照合前へ再登録する必要がある。全候補を常時登録すればこの使用集合の復元は
避けられるが、未使用Prefabを作らない要求と起動時コストに反する。
asset packaging、製品用MODとの境界、使用variantの早期登録を先に決め、
preview専用の永続化を先行実装しない。

現在のLoader開始は`OnLevelLoaded`。本体`NetManager.Data.Deserialize`はそれより前に
`PrefabCollection<NetInfo>.BeginDeserialize/Deserialize`を呼び、`AfterDeserialize`で
`WaitUntilEssentialScenesLoaded`後に`BindPrefabs`を行う。復元経路はこの照合より前に
variantを登録する必要がある。Unity Objectの作成thread、loading queue、他MODの
ロード順も確認する。Runtimeを早くStartするだけの修正はしない。

配置UIはBase Road一覧と共通の一時optionsに分ける。新variantを内部登録しても
Toolbarには追加しない。`NetTool.Prefab`だけをvariantへ切り替え、既設道路のPrefabを
書き換えない。使用variantの解決結果を保存対象とし、未使用の全直積をPrefab化しない。
復元元のbundle更新で既設variantが別断面になることも避ける必要がある。

## 検証と残る完了条件

`python -m unittest tests.placement_section_test`は独立したm値のgolden caseと
semantic matrixを使う。centered/offset、同実幅への拡張、median/roadside、D0/D30、
一通/対面、mirror、未知入力拒否、現14交通構成を検査する。
このgreenはBlender mesh、CS1接続、zoning生成、保存復元を証明しない。

`tests/blender_placement_smoke.py`は独立した座標の期待値を生成後の実頂点へ照合する。
非対称1+2、offset/median付き2+2、D0、広幅一通、offset/median付き4+4を全5modeで検査する。
median下の路面欠落、zero-area face/end capの欠落、通常previewの手編集保全、
identity再利用、生成入力変更後の古いcache拒否と明示rebuildも対象にする。

`test-runtime-contract.ps1`はBlenderのplacement smokeも実行し、実snapshotを.NET readerへ渡す。
参照表読込み時の遅延load、同断面の再利用、mutable consumerとの隔離、lane位置/幅の独立期待値、
誤ったtraffic boundaryの拒否を検査する。内部登録APIの存在やoffline拒否検査を、ゲーム内登録の
成功証拠とは扱わない。

次は非対称native nodeとRuntime登録の実ゲーム確認、NetTool/UI、
transition、保存復元へ接続する。独立oracleへ実際の生成物を渡す検査が必要。
実ゲームではwidth切替、divider、median、roadside、D0/D30、transition、異幅接続、
zone grid alignmentと微小隙間、連続敷設の直線性、node continuity、保存→ロードを確認する。
現在ゲームは起動しておらず、これらは未確認。目的全体は未完了である。

## Expected / Actual impact（この作業単位）

### Network meshの重複割当削減: Expected impact

- 分類: Software Designのresource ownershipとimmutable flyweight。
  数値・形状のownerはcompiled Blender snapshotのまま。Runtimeは完全一致したmeshだけを共有する。
- `PrefabUpdater`からのimmutable配置Prefab用network mesh生成を、同じupdater内のcontent cacheへ集約する。
  頂点、法線、UV、三角形に加え、native `InitSegmentInfo/InitNodeInfo`がmeshへ書くboundsの
  入力（entry種別、min/max height、segment max slope）をkeyへ含める。
  mesh名・material名・UI option名では形状を区別しない。異なるbounds間では共有しない。
- nativeが複製・変更するsegment/node用Materialは共有しない。Prop meshの生成、既設Prefab、
  lane/IMT、AR selector、authoring/export schemaは変更しない。
  編集を繰り返す通常previewはcache対象にせず、編集履歴を強参照で保持しない。
  高さ共有の組立てでは中間D30 subsetのInitializePrefabを省き、完成したselector配列を
  一度だけ初期化する。nativeが生成する描画用Materialの二重割当を避ける。
- 検査は実compiled height familyの各entryを生成portへ渡し、再要求で生成数が増えないこと、
  独立した頂点/法線/UV/三角形/bounds差で別生成になることを確認する。
  portのoffline stand-inはUnity/GPU memoryを測った証拠ではない。
- 横断面ごとのNetInfo分離は今回のmesh共有では減らない。node変形とlane consumerへの対応を
  省略して横断面をまとめることはしない。
- Actual: immutable配置経路だけをcontent cacheへ接続し、通常previewとPropは従来経路に保った。
  literalなbuffer/bounds差のmatrixと、実Blender jp-1x2l高さ4状態のentryで検査した。
  同じnative bounds条件を与えた54 entry要求は18 mesh生成要求へ集約され、10回の再要求で
  増加0だった。これは生成portの件数で、ゲーム内のmode別boundsやRAM/GPU memoryではない。
- Delta: Runtimeの生成cache、shared familyの中間初期化、独立C# contractに限定した。
  原本bufferは変更せず、meshの近似、native描画Materialの共有、NetInfo数の追加削減はしていない。

### AR初期化順の再試行: Expected impact

- installed AR 4.0.0は`LoadingWrapper.OnLevelLoaded`のfinalizerから`LifeCycle.Load()`を呼ぶ。
  RuntimeのOnLevelLoaded内ではAR描画hookがまだ無い場合がある。最初の失敗だけで共有を
  恒久無効にせず、配置要求時に限って必要なhookを再検査する。
- activation中の二重queueを防ぎ、Stop後は再開しない。毎秒・毎frameの再試行と、
  AR無しの大量Prefab fallbackは追加しない。ownerは既存SharedHeightHook/Patchのまま。
- Actual: 初期activation失敗後も配置要求で再検査できるようにし、activation中の重複予約を
  抑止した。cached familyと同じoptionへの再選択もcapability gateを通す。
  cache済みPrefabがAR無しで返るfixtureは修正前に失敗し、修正後はnative生成前に拒否する。
- Delta: 既存activation/controllerの修正のみ。ARの実Harmony呼出しとゲームロード中の順序は
  操作して検証していない。根拠はinstalled DLLのload lifecycleとoffline拒否契約である。

### 高さ4状態のNetInfo共有: Expected impact

- 分類: Software Designのflyweight/shared immutable dataとinstance state、SCMのasset identity。
  最初の共有対象は同一水平断面のD30、D0、D30→D0、D0→D30。4状態ごとの5mode NetInfoを
  1組の5modeへまとめる。幅・divider・median・roadsideまで共有済みとは扱わない。
- compiledの両端と両方向transitionを1組として検査し、NetInfoに必要な共通metadataが一致する
  ことを確認する。高さだけをRuntimeでpresetから再解決せず、compiled lane endpointを読む。
- AR segmentの2 custom bitが状態を所有し、segment-endの1 bitがnodeのD0/D30表示を選ぶ。
  shared geometry entryには不変の条件を設定する。設定操作でshared lane配列/geometryを変更しない。
  ARのmap保存を再利用し、同じ値の別storeやpreview専用map保存は追加しない。
- ARが無い/必要なhookが成立しない場合、別NetInfoの大量生成へ黙ってfallbackしない。
  共有経路の選択を明示的に拒否し、選択中の道路を保つ。既存試作Prefabは削除しない。
- Expected modules: 小さいAR adapter、既存PrefabUpdater/高さhook/配置controller/作成hook、
  独立contract。Native登録数、geometryの参照共有、A/B非干渉とhook対象を個別に検査する。
  ARのNetTool previewはcustom flagsを通常渡さないため、その限定preview selectorも必要である。
- Risk: NetInfo.m_surfaceLevelのconsumer、本体nodeのentry選択、AR保存後の再評価、copy/split、
  hot reload。他consumerを調べずlane curveだけで共有完了とは扱わない。
- Actual: 高さ4状態を1つのfamily identityへまとめ、既存配置controllerからこの登録経路を
  使用する。ARの2 segment bitと1 endpoint bitで選択し、lane更新と`GenerateBezier`の
  surface-level consumerだけをsegment別に補正する。ARのnode selectorと限定preview selectorも
  接続した。通常の両端node meshは2組だけを持ち、transition専用node meshは追加しない。
- Native登録portの独立検査では100回の切替でfactory呼出し1回・5mode要求を確認した。
  これは管理された登録要求の検査であり、Unity内の実生成数・実メモリ計測とは区別する。
  個別flag writerは他segment/他bitの不変と、ARがmanagerを交換した後の新buffer参照を検査した。
- Delta: 新しいlane設定storeやmap保存adapterは追加していない。ARが有効で、描画/LODの
  必要なtranspilerが存在することをNative登録前に確認する。不成立時に旧大量NetInfo経路へ
  fallbackしない。未完の初期化結果も再利用しない。
- 残る範囲: 幅/divider/median/roadsideの共有、CRPへのcompiled data格納と早期登録、
  実際のAR/Harmony呼出し、native描画、他MODを含むCRUD。実ゲーム確認は今回のgateから
  外れているが、offline port検査をその確認済み扱いにはしない。

### segment個別lane反映の最初の作業単位

- 分類: Software Designのshared stateとinstance stateの分離、Testingの非干渉検査。
- Expected: 既存の高さtransition lane反映を、segment IDとそのlane chainを明示した
  bounded adapterへ変更する。全laneの所属と入力を先に検査し、他segmentや共有lane定義へ
  書き込まない。不正chainの途中まで更新する経路をなくす。
- 1回の反映は当該lane数に比例する2回のchain traversalだけ。街全体の探索、tick、
  JSON/texture読込み、per-lane配列割当を追加しない。高さの数値ownerはcompiled snapshotのまま。
- Expected modules: 既存高さadapterと独立C# contract。authoring schema、mesh、UI、Loader、
  AR保存dataは変更しない。これはAR表示selector接続や全physical lane consumerへの
  対応とは別の作業単位であり、AR対応全体を完了したとは扱わない。
- Primary proof: A/Bが同じlane定義を参照してもAのみ変化、入力array不変、逆向きと
  D0/D30/歩道のliteral期待値、lane所属不一致/短いchain/余分なchain/循環で全書込みなし、
  native再構築からの再適用が同じ結果。大きいbufferでも同じ対象だけを反映する。
- Actual: 高さ補正のproduction callbackが実segment IDを渡し、全chainの所属・長さと
  endpoint値をpreflightした後に当該laneだけを更新する。通常経路のlaneごとのfloat配列を
  除いた。独立C# fixtureで同一定義を使う別segmentとshared lane/endpoint入力の不変を確認し、
  不正chain/非有限値/overflowの拒否で部分書込みがないことを検査する。
- Delta: 高さadapterとcontractに限定し、新しいstate store、AR条件、UI、mesh、Loaderは
  追加していない。AR接続、横位置/幅の全consumer、CRUD継承は後続であり、未完了のまま。
  管理された2lane反映10万回の初回計測はbuffer容量16で37.991 ms、262144で36.368 ms。
  これは当該kernelのoffline計測で、街全体のFPS、AR処理、描画costを証明しない。

- 分類: Software Architecture/Designのconfiguration variabilityと
  Software Configuration Managementのidentity/lifecycle、Testingの独立oracle。
- Expected: domainだけに断面判断を追加し、保存schema・既存bundle・既設道路を変えない。
  新profile system、appearance skin、UI側の二重計算を導入しない。
- Actual: `domain.py`のresolver/値型/identityと独立Python testを追加した。
  adaptersは未接続。文書案内と`arch_lint.py`へ新資料の役割だけを登録した。
  新しいpreset値や接続判断をlintへ固定していない。既存14道路やRuntime/Loaderの動作変更はない。
- Delta: ownerと依存方向の予定外の拡大はない。CS1のnode非対称性とロード順が
  残る境界であり、domain testをその代替証拠にしない。

### Blender接続の作業単位

- Expected: `domain.py`の解決結果をBlender adapterへ渡す。左右curb入力を共通surface helperへ
  追加し、全5modeの構造helperを再利用する。通常preview、JSON schema、Runtime登録は変えない。
- Actual: `build_placement_mode`と専用collectionのtransaction/cacheを追加した。
  `domain.py`にはNONEからmedianを有効化する場合の形状選択だけを追加した。
  既存surface helperはcurbの左右端を任意入力として受け取るが、従来callerの既定値は対称のまま。
- Delta: ロード済みNetInfoへの適用やUI変更はない。実ゲーム接続の未検証範囲はそのまま残る。

### Runtime受渡しの作業単位

- Expected: domain/Blenderからimmutable snapshotを出し、Runtimeは選ばれたデータだけを読む。
  新しい継承system、Runtime側のpreset計算、通常manifestのvariant追加はしない。
- Actual: export関数、`PlacementVariantLibrary`、nullable field reader、内部Prefab登録APIを追加した。
  height/sidewalk geometryはdomainで導出し、通常exportは同じserializer ownerを使う。
  テストコマンドは実Blender snapshotを入力にする。
- Delta: 配置UIやsave/loadにはまだ接続していない。非対称nodeの拒否は一時的な安全境界であり、
  全Base Roadが配置可能になったという完了主張ではない。

### 配置候補の取得経路

- Expected: 選択可能な組合せと解決済み実幅のownerをdomainに保ち、Runtime UIは
  compiled indexだけから候補を取得する。候補一覧の取得ではmesh JSONを読まない。
  indexの実幅は表示用の派生値で、snapshot読込み時に断面と照合する。
  既存authoring schema、通常manifest、既設Prefab、線種の所有は変更しない。
- 検査: 対面の162組合せと一通の18組合せを独立したliteral一覧で照合する。
  一通のoffset/medianを黙って置換せず候補から除き、不正なlane構成自体は拒否する。
  Runtimeでは候補DTOの変更がindex cacheへ戻らないことも検査する。
- Actual: `placement_option_matrix`が全選択肢と断面を返し、export adapterは要求全件を
  先に検査してからsnapshotを出力する。試作indexをschema 2にし、実幅・高さ・交通分界の
  表示値を同じ解決済み断面から追加した。Runtimeの`GetChoices`は独立DTOだけを返す。
  選択時に表示値をsnapshotへ照合し、相違したJSONをcacheへ入れない。
- Delta: indexの試作formatだけを更新した。snapshot format、authoring schema、通常manifest、
  Prefab登録、実ゲームUIの動作は変更していない。全候補のcompileはAPIで要求できるが、
  未使用全候補のPrefab化やToolbar登録はしない。UIへの接続は未完了である。

### 配置パネル接続のExpected impact

- 共通の一時selectionを1件だけ持ち、Base Roadごとのselectionを永続化しない。
  選択変更は提案→snapshot検証→内部Prefab登録→NetTool選択の順で行い、失敗時は
  元の共通selectionとtool prefabを保つ。未compile候補を別断面へ置換しない。
- Runtimeの既存配置パネルへ5項目を追加する。候補と実幅はindexから取得し、
  mode対応はBase Road/内部variantの5modeリンクから取得する。線種は従来の共通選択のまま。
- 通常manifestや既設Prefabの変更、全segment走査、Node Controllerの設定補正は行わない。
  非対称nodeの未実装gateと保存復元・transitionの未完了は、このUI接続で解消した扱いにしない。

- Actual: `RoadPlacementGeometryController`が共通selectionとBase Road/modeの対応を持つ。
  同じroadを選び直しても全候補はPrefab化せず、使用したsnapshotだけを登録する。
  indexの`default_key`はexport ownerが出力し、初期selectionの設定にだけ使う。
  一通へ変えて既存選択が使えない場合はエラーを表示し、必要ならユーザーが
  `Use Base Road default section`で明示的にリセットする。個別項目では直せない複数の
  無効値が残った場合にも、この明示操作で戻れる。
- Delta: 既存panel、Runtime開始/road登録、IMT target登録へ限定接続した。
  IMT登録は新variantだけを追加し、既存segmentの生成線ownershipを破棄しない。
  この作業単位では通常exportへの全候補index出力は未接続だった。下記の一括export接続で対応した。
  shader/nodeや保存復元を修正したとは主張しない。実ゲームpanelは未確認。

### 通常library exportへのExpected impact

- 一括exportで各Base Roadの全compiled候補indexを出し、通常manifestには従来の
  Base Roadだけを残す。Runtimeのgeometry計算を追加せず、Blender adapterが全modeを出す。
- snapshotの出力後、一括compileで新しく作った作業用collectionとmeshだけを解放する。
  既存placement previewやその手編集は残す。texture stagingは一回のcompileで共有する。
- full-menuの新規生成物がSceneへ残らないことと、通常library exportが全候補を持つことを
  実Blenderで検査する。保存復元や非対称nodeの未完了を一括export成功で代用しない。

- Actual: library exportが全道路の候補indexを作り、manifestには参照pathとindex hashだけを
  追加する。menu-onlyの更新はBase Road bundleのrevisionを変更しなくても検知し、
  Runtimeは候補indexを照合して更新する。登録済みvariantの描画データは書き換えない。
  現在toolで選択中のvariantは、同じoption keyでも新snapshot identityがあれば
  次の配置用Prefabへ切り替える。共通selectionは保持し、既存生成線のstampを破棄しない。
- Delta: exporter、manifest DTO、Runtime menu登録、panelのselection観測に限定した。
  全segmentを調べる経路、新たな継承/profile、geometryのRuntime再生成は追加していない。

### 一括compileのRNAアクセス

一括compileの5秒samplingでは302 sample中、`serialize_mesh_object`が127、
`_mesh_object`が78 sampleの最上位frameだった。これは当該区間の観測で、全時間の割合ではない。
mesh単位でRNA配列を一括読込み・書込みするよう変更し、3種類の実fixtureのsnapshot identityは
変更前と完全一致した。placement smokeの実行時間は変更後32.27秒だった。
これだけで全道路や実ゲームの速度改善率を保証しない。

### snapshot圧縮のExpected impact

一括候補出力のdisk使用量を減らすため、placement snapshotだけをdeterministic gzipで出す。
JSON payloadとidentityは同じまま、indexは圧縮fileのhashを持つ。通常road bundleは変更しない。
Runtime consumerはraw hashの照合後に展開し、展開後も既存の32 MiB上限を守る。
既存の未圧縮snapshotも読めるようにし、過去の出力は削除しない。
独立したlane/geometry oracleは形式に関係なく同じfixtureを検査する。

- Actual: `.json.gz`へ出力し、gzip headerのmtime/filenameを固定した。同じ入力の
  再出力は同じfile bytesになる。Runtimeはhash照合後、上限付きで展開する。
  Plain JSON reader互換は維持し、固定gzip bytesと容量超過の拒否も別検査する。
- 観測: 一通18候補のunique snapshotで、非圧縮JSON合計25,345,732 bytesに対し
  圧縮file合計1,826,857 bytesだった。これはこのfixtureの値で、他の道路を保証しない。
- Delta: placement snapshotの搬送形式だけを変更した。geometry identity、lane配置、
  authoring schema、通常道路bundle、RoadImporterは変更していない。

### 非対称断面の登録: Expected impact

- 分類: Software Designの過剰なcapability preconditionと、rendering座標系の参照値。
  physical断面とshader参照の混同を解消する。新profile/継承やnode走査frameworkは作らない。
- `domain.py`がshader参照pavement幅を左右実歩道幅の平均として導出する。
  実curb、左右歩道、lane座標は変更しない。snapshot schema 2でその参照値を明示する。
- Runtimeは参照値を受け取り、左右が異なることだけを理由にPrefab登録を拒否しない。
  schema 1の既存対称snapshotは以前の参照値を再現する。参照値がない旧非対称snapshotは
  推測せず再exportを要求する。既設NetInfoを上書きしない。
- `tests/native_node_reference_test.py`はインストール済みRoadBridgeのglcore NET_NODE式から
  独立したparameter oracleを持つ。同参照の恒等写像と、異断面での不一致例を分ける。
  Direct3Dの実描画、control matrix、曲線、交差点での一致をこのtestで証明したとはしない。
- Expected modules: domain、Blender snapshot serializer、Runtime bundle adapter/reader、関連testのみ。
  Node Controller、UI、marking、zoning、loader保存復元の動作は変更しない。

- Actual: shader参照値のownerをdomainの読み取り専用propertyに置き、snapshot schema 2へ
  明示出力した。Runtimeのmanaged bundle compilerがそれを適用する。schema 1の対称
  snapshotは元の参照値を再現し、旧非対称snapshotは再exportなしに補完しない。
- Delta: physical断面、authoring schema、通常bundle、marking、zoning、Node Controllerは
  変更していない。Native登録の成功・実描画は未検証。managed compiler検査はNetInfo登録の
  成功を証明するものではない。
- 確認した限界: 同幅32 mで1+2のcurb -3/+6と対称curb +/-12を混在させた場合、
  glcore側のlateral parameterを矩形control frameで評価すると -5.5/+9.260869...
  となり、両実断面の中間 -7.5/+9と一致しない。参照の分離だけでmixed nodeを完了扱いにしない。

### 高さtransition meshのExpected impact

- domainが両端の断面とlane高さを所有する。今回のtransitionは同じ水平断面のD30/D0間に
  限定し、幅・lane・median等を同時に変更する要求は拒否する。通常nodeで吸収できる差の
  分類を、この限定実装で確定したとは扱わない。
- 64 mのsegmentは両端のroadway高さを線形に結ぶ。歩道・床版の基準高さは0を保つ。
  start/end nodeはそれぞれの通常断面を使い、第三の恒常的road levelを作らない。
- Blenderの既存surface/median/構造helperを再利用し、curb壁がD0端で消えるところだけ
  退化quadをtriangleにする。通常preview、既設Prefab、Node Controllerの設定は変えない。
- Expected modules: domain、Blender adapter、geometry smoke。Runtime lane trajectory、
  一回だけの配置状態遷移、save/loadは後続で接続し、mesh成功で完了扱いにしない。

- Actual: 両方向・全5modeのsegmentと通常の両端nodeを生成し、生成頂点を独立した
  数値期待値で確認した。D0端で消えるcurb壁は退化quadでなくtriangleになる。
- Delta: pure geometry helper、domain、Blender adapterに限定した。通常のroad levelや
  Runtime lane軌道を変更していない。これをゲーム内のtransition成功とは扱わない。

### 高さtransitionの受け渡し: Expected impact

- 分類: Software Designのrepresentation boundaryと参照整合性。
  両端の通常snapshotを再利用し、第三の恒常的road levelを保存しない。
- indexの通常Choicesとは別にHeightTransitionsを置く。FromKey/ToKeyは同一水平断面の
  既存choiceを参照し、transition snapshotはStartIdentity/EndIdentityとsegment meshだけを持つ。
  lane、node、断面寸法の複製正本は作らない。数値のownerは既存domainを維持する。
- 全候補exportは両levelが存在する組合せだけをcompileし、解決後の同じ両端identityは共有する。
  旧indexにtransitionがない場合は操作不可とし、通常segmentを代用しない。
- Runtime readerは選択された両端とsegmentをhash照合して読む。水平断面・lane metadataの
  相違、端点参照違い、nodeを混ぜたtransitionを拒否する。native登録は後続のendpoint/lane
  adapterが必要であり、このreaderを通常Prefab compilerへ渡す経路は追加しない。
- Expected modules: Blender exporter、既存PlacementVariantLibrary、関連smokeのみ。
  UI、ロード復元、Node Controller、通常snapshot schemaは変更しない。

- Actual: 通常choiceのindexに両端keyを参照する別リストを追加し、transitionは全5modeの
  segmentだけを出力した。Runtimeは両端通常snapshotとtransitionのhash・identityを照合し、
  独立したDTOを返す。水平断面、lane metadata、端点参照、node混入の拒否を実fixtureで検査した。
- Delta: 既存serializerのcompact化とbounded/hash readerを共用した。通常snapshotのschemaは2の
  まま、transition snapshotは別型のschema 1。Runtime Prefab compilerへまだ接続していない。
  全候補exportの新規生成物は引き続き解放し、既存previewの手編集は保持する。

### Native高さtransition: Expected impact

- 分類: Software Designのadapterとoperation transaction。両端snapshotが数値を所有し、
  Runtimeはmesh、lane軌道、endpoint selectorへ変換する。新preset/profileは作らない。
- transition Prefabは通常の両端Prefabを参照し、segmentは専用mesh、nodeは両端の既存meshを
  参照する。通常Prefabや既設segmentのgeometryを上書きしない。
- 描画向きはsegment entryのflagsから決め、lane curveへ両端の高さ差を線形に足す。
  交通方向で始点・終点を入れ替えない。更新対象は当該transitionのlane chainだけとする。
- NetToolの配置operation内だけに一回限りの消費状態を持つ。既存のCreateSegment hookを
  拡張し、最初に成功したtransitionの後は通常target Prefabへ渡す。試験、preview、失敗、
  他Prefabのsplitは消費しない。Node Controllerのposition/height/角度/flagには書き込まない。
- Expected modules: PrefabUpdater、専用高さadapter、既存作成hook、配置controller/panel、
  lifecycle、managed contract。Blender authoringとsnapshot schema、zoning、保存復元は変えない。
- 実ゲームのlane/node連続性とNetToolの操作は別のgate。managed testだけで完了扱いにしない。

- Actual: 両端Prefabを参照する専用transitionを登録するadapter、lane curveへの高さ差加算、
  描画向きに対応した両端node selectorと通常node選択を接続した。更新対象は当該lane chainと
  nodeの最大8接続のみ。Node Controllerの編集値には書き込まない。
- 一回限りのoperationはNetToolの実作成呼出しで開始し、既存CreateSegment prefix/postfixを
  通じて最初の成功だけ消費する。後続は通常target Prefabを使い、UIの既存watcherで次の
  操作をtarget levelへ切り替える。試験、visualize、Move Itなど別toolのコピーは消費しない。
- Invertの場合は逆向きのcompiled transitionを使い、meshの反転とlane端点の順序を一致させる。
  既知の生成variant同士のD0/D30直接接続は作成前に拒否する。外部道路のlocal高さは推測しない。
- Delta: 通常authoring、Blender serializer、snapshot schemaは変更していない。5modeの参照
  projectionは既存ModeTemplateへ集約した。component tagはimmutableなPrefabに持たせ、
  hot DLL後も型名から参照できる。mapロード時の早期登録をこのtagだけで証明したとはしない。
- 未検証: Native Prefab登録、実NetToolのmulti-segment操作、lane/node描画、他MODとの順序。
  長いstrokeのpreviewは通常のNetTool描画経路であり、最初の1本だけをtransitionにする
  実作成結果との一致は未確認。保存復元、異断面node補正、zoningも引き続き別gateとする。

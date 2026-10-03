# 検証方針

> 文書種別: 検証

道路定義、導出規則、Blender生成、Runtime、CS1表示を別の証拠で確認する。
テスト数は完成条件にしない。異なるfault classを同じsmoke testで代用しない。

## 変更と必要な証拠

| 変更対象 | 最低限の検証 | 証明しないこと |
| --- | --- | --- |
| domainの導出規則 | domain contract test | Blender meshとCS1表示 |
| geometry plan | geometry plan test | Blender APIによる生成結果 |
| dependencyとowner | architecture lint | 実行時の正しさ |
| Blender UI、mesh、UV | Blender background smoke | Asset Editorとゲーム内shader |
| RoadImporter | importer build | CRPのゲーム内表示 |
| Runtime bundleとreader | Runtime contract smoke | Prefab登録、map再読込 |
| CS1描画と接続 | 実ゲーム確認 | 未確認の他modeや他MOD連携 |

## Primary proof

Blenderに依存しない判断は、次のテストで確認する。

```powershell
python -m unittest tests.domain_contract_test tests.geometry_plan_test
```

主な検証対象は次のとおり。

- 2、3、4車線の幅をlane入力から導出する。
- boundary identityをstable lane IDと隣接関係から導出する。
- lane追加時も無関係なedge boundary IDを維持する。
- segmentとnodeの長さ、slice数にownerが一つだけ存在する。
- 主桁は偶数本で、JIS標準の2.6、3.2、3.8 m間隔から選ぶ。
- 主桁中心は左右対称で、床版内に収める。

## Structural proof

`tools/arch_manifest.json`はsourceをlayerへ一度だけ分類する。
`tools/arch_lint.py`は依存方向、主要定数のowner、文書一覧を検査する。

```powershell
python tools/arch_lint.py
python tools/harness/test_architecture_lint.py
python -m unittest tests.architecture_harness_test
```

portable self-testは合成fixtureを使う。
manifest validation、required root、分類、再帰pattern、禁止tokenを確認する。
token guardはbehaviorを証明しないため、geometry testの代用にはしない。

## Representative end-to-end

Blenderの代表経路はbackground Blenderで確認する。

```powershell
.\scripts\run-blender.ps1 -Script .\tests\blender_addon_smoke.py
```

このsmokeは次の経路を対象にする。

- JSON importとround-trip
- v3からv4への保全migration、profile入出力、正確なlane metadata tupleの既定値解決
- migration、import、duplicateでの同値override除去と異値override保全
- 片方向0〜4・合計最大8の14 vehicle-lane variant（14道路・88 lane）とcatalog/bundle lane一致
- Scene内の複数道路一覧、追加、複製、削除、Reload Scripts後のactive road
- 全5 modeの生成
- 64 m長、slice、node中央分割
- UV範囲とmarking切替
- Elevatedの主桁とカスタム端部
- texture fixtureとRuntime bundle

texture fixtureは`textures/atlas_bases`を
`build/smoke/texture-fixture`へ複写して使う。
Photoshop Generatorの保存処理とは競合させない。

形状確認用renderは補助証拠として生成できる。

```powershell
.\scripts\run-blender.ps1 -Script .\tests\render_geometry_preview.py
```

render画像だけで寸法、topology、UVの正しさを判定しない。

## RoadImporter

RoadImporterを変更した場合はbuildを実行する。

```powershell
Set-Location <repository-root>
.\scripts\build-road-importer.ps1
```

build成功はAsset Editorへのimportや、ゲーム内表示を証明しない。

## Runtime preview

Runtime変更ではcatalog、Blender bundle、.NET reader、CS1参照build、stagingを分けて確認する。

```powershell
python -m unittest tests.runtime_catalog_test
.\scripts\run-blender.ps1 -Script .\tests\blender_addon_smoke.py
$previewPath = Join-Path $PWD 'build\smoke\runtime-preview'
.\scripts\stage-runtime-host.ps1 -PreviewPath $previewPath
.\scripts\test-runtime-contract.ps1 -PreviewPath $previewPath
```

`RUNTIME_CONTRACT_OK`は、BlenderのJSONを.NET 3.5 readerで読めることを示す。
成功事象と意図的な型不一致が、専用JSON Linesログへ分類されることも確認する。
Blender smokeは、Scene共通の出力先へ全道路を一括Runtime exportし、manifestがScene内道路だけを含むことも確認する。
catalogとbundleのlane不一致は`lane_contract_mismatch`として拒否する。
一致時はcatalog laneへ置換せず、比較したBlender bundle lane配列そのものをRuntime適用へ返す。
配置時の3規則が共通の一時選択からsegment用style snapshotへ変換されること、CS1用panel型が存在すること、
全segment走査経路がなく、作成と削除の両hookが存在することもreflection contractで確認する。2-segment nodeでは、
路側帯線・中央線・流入/流出separatorの接続可否、1～4車線の全16組合せ、端点を重複使用しないこと、対面道路から一方通行道路へ中央線を接続しないこと、135度未満だけ破線を実線化する境界をcontractに含める。
IMT entrance更新が接続segmentより遅れた場合は部分生成せず、IMT本体の`IMT.Manager.MarkingManager.Update()`完了後に未成立だった対象nodeだけを再投入する。Runtimeフレームやsimulation tickを監視して再試行する設計ではなく、全node走査もしない。
参照道路から矢印・信号lane propを抽出する経路と、TM:PEへ信号既定ONを要求する経路の存在も確認する。

### Runtime markingの独立検証

`tests/runtime_marking_reference.py`はproductionの`BoundaryRoles`、`MatchBoundaryRoles`、
`ImtNodePolicy`をimportしない。`NetInfo lane`相当のfinal direction、segmentのstart/endとInvert、
IMT PointSourceのLeft/RightIndex、node entranceをraw snapshotとして受け、道路の意味から
`BoundaryMeaning`と`MarkingPlan`を作る。対応するテストは車線数を仕様分岐にせず、one-way、two-way、
incoming/outgoing、Invert、lane transition、2-segment接続、3-way交差点、未完成entranceを性質として検査する。

このreference testのgreenは、productionのIMT adapterや実ゲームの表示を証明しない。逆に、
Invert無視、one-wayへのcenter生成、center/separatorの取り違え、物理左右とlane indexの混同、
不完全entranceの受け入れを意図的に入れたときに失敗することを、ハーネス自体の検査として含める。

既存の14道路・88 lane検査、`ExpectedCrosswalkCount`、role配列を直接渡すmatrix、contract smokeは、
authoring/export契約またはproduction APIの局所契約として扱う。これらのgreenだけではRuntime topologyの
意味解釈、IMT lifecycle、実ゲームのmarking一致を証明しない。

このcontract smokeは次を証明しない。

- `runtime.current`による実ゲーム中の差し替え
- Prefab登録とshader描画
- map save後の再読込
- Adaptive Roads条件の反映
- 実際の接続形状
- 道路ツール上のpanel配置、表示条件、クリック操作

## 最終確認

Asset Editor、shader、AO、selector、カーブ接続は実ゲームで確認する。
ゲームを起動していない作業では、これらを完了扱いにしない。

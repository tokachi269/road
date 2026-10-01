# 契約と検証の対応

この文書は、重要な規則の意味、owner、検証先を結び付けます。
テスト内の固定値を重ねて列挙するための文書ではありません。

## 断面とlane

| 契約 | owner | 検証 |
| --- | --- | --- |
| laneの順序と幅から断面を作る | `domain.py` | `tests/domain_contract_test.py` |
| 収まらない明示寸法を縮めない | `domain.py` | `tests/domain_contract_test.py` |
| 駐車laneは左右分が収まる場合だけ作る | `domain.py` | domain test、Blender smoke |
| boundary IDをlane IDから導出する | `domain.py` | `tests/domain_contract_test.py` |
| 道路線を道路単位の規則から導出する | `domain.py` | domain test、Blender smoke |

断面を変更するときは、UIだけでなくdomain testを先に更新します。

## geometry

| 契約 | owner | 検証 |
| --- | --- | --- |
| segmentとnodeの長さ、slice数 | `domain.py` | domain test、Blender smoke |
| 全modeの基準高さと配置間隔 | Blender add-on | Blender smoke |
| 主桁の本数、間隔、外形 | `geometry_plan.py` | `tests/geometry_plan_test.py` |
| カスタム端部の反転、切断、結合 | Blender add-on | Blender smoke |
| 分離帯の生成と入力mesh利用 | Blender add-on | Blender smoke |

Blender smokeは数値とtopologyを確認します。
見た目の自然さはBlenderまたはCS1での手動確認が必要です。

## UVとtexture

| 契約 | owner | 検証 |
| --- | --- | --- |
| atlas寸法とregion | `textures/dimensions.json` | `tests/texture_layout_test.py` |
| curbに隣接する連続profile | `dimensions.json` | texture test、Blender smoke |
| line slotと実際のUV幅 | `dimensions.json` | texture test、Blender smoke |
| map名とchannel packing | `dimensions.json` | texture test、Runtime contract |
| 生成faceのregion割り当て | Blender add-on | Blender smoke |

PSDは画像制作物です。UV座標のownerにはしません。
制作手順は[テクスチャ制作](texture-authoring.md)を参照してください。

## RuntimeとIMT

| 契約 | owner | 検証 |
| --- | --- | --- |
| catalogの決定的なcompile | catalog compiler | `tests/runtime_catalog_test.py` |
| lane snapshotの一致確認 | RuntimeHost | Runtime contract |
| 新規segmentを単一hookで受け取る | RuntimeHost | architecture test、Runtime contract |
| 定期pollでIMT編集を上書きしない | RuntimeHost | architecture test |
| ゼブラのwall境界と安定した縞数 | IMT adapter | Runtime contract |
| node種別と停止条件 | IMT policy | Runtime contract |

ゲーム内の描画、Move It操作、他MODとの競合は自動テストだけでは確認できません。

## 検証されていない領域

LOD、最終CRP、全selector、map再読込、Adaptive Roads固有flagは未完了です。
これらを「対応済み」と記載しないでください。

# CS1連携の前提

この文書は、外部実装から確認した事実と未検証事項を区別します。
プロジェクト内部の固定値は[契約と検証の対応](contract-map.md)を参照してください。

## 根拠の種類

| 種類 | 意味 |
| --- | --- |
| Importer契約 | RoadImporterのデータモデルで確認した内容 |
| サンプル観測 | RoadImporterのCSUR 8DRサンプルで確認した内容 |
| CSURの実装 | 参考実装であり、CS1全体の必須条件ではない内容 |
| プロジェクト判断 | この生成器が採用した方針 |
| 未検証 | Asset Editorまたは実ゲームで確認が必要な内容 |

参考リポジトリは`references/RoadImporter`と`references/CSUR`に置きます。
生成処理からCSURへ依存しません。

## サンプルから確認したこと

Blenderへ読み込んだサンプルでは、Xが横断、Yが長手、Zが高さでした。
segmentとnodeにはカーブ変形用の長手分割がありました。
確認したFBXにはUV layerと対応するLODがありました。

本プロジェクトの長さとslice数は、サンプル値を基に決めています。
現在値は`domain.py`が所有し、domain testとBlender smokeが検査します。

## Importerへ渡すもの

完成出力には、mesh、LOD、texture、NetInfo、AI、lane、segment、nodeが必要です。
shaderやselectorが違うgeometryは、別entryが必要になる場合があります。

Blender上の`surface`、`structure`、`tunnel`分類だけでは、
CS1 entryの分割を決められません。

## 完成扱いにしない項目

次の項目は未完了または未検証です。

- 全modeのLOD生成
- 最終的なselectorとconnect group
- RoadImporterから作ったCRPのゲーム内確認
- shader、AO、normalの最終表示
- 全種類のnodeとtransition
- save後の再読込
- Adaptive Roads固有flagの反映

Blender previewまたはRuntime previewが動いても、完成道路とは扱いません。

## ゲーム内で確認すること

straight、curve、bend、junction、transitionを確認します。
GroundだけでなくElevated、Bridge、Tunnel Entrance、Tunnelも対象です。

道路幅、歩道接続、信号、prop、zoningは実際の道路と接続して確認します。
見た目だけでなく、lane接続と保存後の復元も確認します。

# 作業状況

この文書は、現在の到達点と残作業を短く示します。
詳細な契約は[契約と検証の対応](contract-map.md)を参照してください。

## 利用できるもの

- Blenderで全5 modeのpreviewを生成できる
- 共通texture atlasとUVを検証できる
- 通常マップ用bundleを出力できる
- LoaderとRuntime DLLを分けて差し替えられる
- catalogから道路とlaneをcompileできる
- IMTへ道路線、停止線、ゼブラの初期値を設定できる
- 専用JSON Linesログで失敗原因を追跡できる

## ゲーム外で検証済み

- Python unit test
- architecture lint
- Blender background smoke
- RoadImporter build
- Runtime stagingとcontract smoke

## 実ゲームで確認が必要

- Runtime差し替えの長時間安定性
- shaderとtextureの最終表示
- map保存後の復元
- 全node形状と接続
- IMT、TM:PE、Move Itとの競合
- Adaptive Roads固有flag

## 次の完成条件

最終CRPにはLOD、selector、AI、全modeのAsset Editor確認が必要です。
Runtime previewの成功を、CRP完成の代わりにはしません。

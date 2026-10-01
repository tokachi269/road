# Runtime preview

この文書は、Blenderの道路を通常マップで差分確認する手順を説明します。
最終CRPを作る機能ではありません。

## 仕組み

Blenderはmesh、lane snapshot、texture参照をbundleへ出力します。
RuntimeHostはbundleを読み、ロード済みPrefabへ反映します。

RuntimeHostは道路断面を再生成しません。
Blenderで編集した頂点、法線、UVをそのまま使います。

## 初回の配置

```powershell
$previewPath = Join-Path $PWD 'build\runtime-preview'
.\scripts\stage-runtime-host.ps1 -PreviewPath $previewPath
.\scripts\install-runtime-host.ps1
```

Loaderを変更した場合は、CS1を終了して再配置します。
Runtime DLLだけを変更した場合は、versioned DLLとして差し替えられます。

## Blenderから出力する

Roadパネルの`Runtime preview`からbundleを出力します。
設定変更時は必要なmodeを再生成してから出力します。

生成meshを直接編集した場合は、再生成せず現在のmeshを出力できます。
textureだけを更新した場合は、meshを作り直しません。

## catalog

`catalog/*.tsv`は表計算ソフトで編集できます。
道路、lane、prop、配置、条件、geometry binding、試験scenarioを分けています。

RuntimeHostはcatalogとBlender bundleのlane snapshotを比較します。
一致しない場合は、Runtime側で補正せず道路全体を拒否します。

## 更新範囲

表示やmaterialの変更では、既設segmentを削除しません。
lane構造を変えた場合も、ユーザが敷設した道路は自動削除しません。

新規segmentはCS1の作成イベントから受け取ります。
毎frameまたは毎秒の全segment走査は行いません。

## IMT

RuntimeHostは新しい道路へ線、停止線、ゼブラの初期値を設定します。
その後の個別編集と保存はIMTが所有します。

IMTで削除した線を定期的に復元しません。
既定値へ戻す場合は、IMT内の`Restore road defaults`を明示的に使います。

## 診断ログ

専用ログはJSON Lines形式です。
`event`は機械検索用、`message`は人が読む説明に使います。

同じrevisionの失敗は繰り返し出力しません。
入力が変わるか、手動で再試行した場合だけ再処理します。

## 読み取り専用のPrefab確認

`find_net`は候補名とlane数を返します。
`inspect_net`の応答は、指定した1道路の1区分だけです。

全Prefabを一括dumpしません。
応答は調査用であり、道路定義として保存しません。

## 未検証

Prefab登録、shader表示、map再読込、Adaptive Roads連携は実ゲーム確認が必要です。
確認方法は[検証方法](testing.md)を参照してください。

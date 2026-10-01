# Runtime preview 作業表

> 文書種別: 状態記録

この文書は作成時点の進捗記録であり、現在の仕様や完成状態を保証しない。
現行の責務は[構成と責務](architecture.md)、検証手順は[検証方針](testing.md)を参照する。

この表は通常マップ上で道路を差分確認する開発経路の実装状況を管理する。Asset Editorを使った最終CRP作成とは分離する。未検証事項を仕様として確定しない。

## 完了条件

- Blenderから同名道路のpreview bundleをatomicに書き出せる。
- 表形式の正本から道路、lane、prop配置、条件、接続試験をまとめて検証・compileできる。
- 通常マップ用Hostが約1秒間隔で変更を検出し、変更された道路だけを処理する。
- 表示・材質・配置値の変更では登録済み`NetInfo` / `PropInfo`のidentityを維持する。
- lane構造が変わった場合だけ、その道路が所有する自動試験区画を作り直す。
- 起動中のHost実装を、ゲーム本体を再起動せずに新しいDLLへ切り替えられる。
- 実ゲームを起動しなくても、Python test、Blender 5.1 smoke、C# build、staging smokeが通る。

実ゲームでのshader、保存データ再読込、Adaptive Roads連携、他MODとの競合は今回の環境では未検証と明記する。

## 作業表

| ID | 作業 | 状態 | 検証 |
|---|---|---|---|
| R0 | 既存Elevated端部Mesh変更を独立commit | 完了 | `6bd61b2`、既存12 test、Blender smoke |
| R1 | 表形式catalogと決定的な優先順位生成 | 実装済み | `runtime_catalog_test`、sample compile |
| R2 | atomic bundle、road単位revision、構造signature | 実装済み | unit test、Blender smoke |
| R3 | Blenderの手動exportと1秒debounce auto export | 実装済み | Blender 5.1 background smoke |
| R4 | 安定Loaderとversioned Runtime DLLのhot reload | build/publish済み・ゲーム待ち | reflection contract、hash付きpointer一致。ゲーム内切替は未確認 |
| R5 | 通常マップHostのmanifest監視とroad単位差分適用 | build済み・ゲーム待ち | C# build、.NET contract smoke。ゲーム内pollは未確認 |
| R6 | `NetInfo` / mesh / materialのin-place更新 | build済み・ゲーム待ち | compile済み。Prefab登録と描画は未確認 |
| R7 | Prop/Decal catalogと道路laneへの配置 | build済み・ゲーム待ち | Blender Prop bundle、C# compile。shader表示は未確認 |
| R8 | 接続・曲線・交差点の自動試験区画と限定再生成 | build済み・ゲーム待ち | CS1 APIに対してcompile。実配置は未確認 |
| R9 | Adaptive Roads向け条件を失わない中立モデル | 要求範囲実装 | vanilla flag適用、未知namespace保持。AN adapter自体は未実装 |
| R10 | build/stage/runbookと日本語設計文書 | 実装済み | stagingとrunbook確認 |
| R11 | 全自動検証 | 完了（ゲーム外） | environment、arch lint、16 unit、Blender 5.1、RoadImporter、Runtime build/stage/contract |
| R12 | 画面を見ずに追跡できる専用診断ログ | 実装済み・ゲーム待ち | JSONL出力、原因分類、road/revision/path/段階、例外stack、型不一致contract smoke。ゲーム内の実ファイル出力は未確認 |

## 確定している境界

- 編集正本、Blender geometry、CS1 runtime entryを同一データ構造にしない。
- Runtime HostはRoadImporterへ混ぜない。RoadImporterはAsset Editor出力経路として残す。
- hot reloadは安定Loaderがversioned Runtime DLLを読み替える方式にする。.NET/Monoの読み込み済みAssembly自体はunloadできないため、切替時に旧Runtimeのcallbackと所有物を停止する。
- 道路やPropの名前は公開・ローカルのファイル名ではなく、catalog内のstable IDで参照する。実行時に解決したPrefab名をbundleへ記録する。
- Adaptive Roads固有flagはcoreの意味を決める入力にしない。条件のnamespaceと値を保持し、利用可能なadapterがある場合だけ適用する。

## 未確定・実証待ち

- Asset Editorを一度も通さず生成した道路を含むmap saveが、全ロード順序で安定して復元できるか。
- Adaptive Roadsの全extension flagをruntimeで安全に更新できるか。
- DecalおよびPloppable Asphalt相当shaderの必須fieldと、公開Workshop IDへ変わったPropの最終解決方法。
- segment/nodeのgeometry entry分割、atlas数、AO表現の最終形。
- ゲーム内で既設segmentを残したままlane数を変えることの安全性。previewでは既設本線を直接変えず、Host所有の試験区画だけを再生成する。

これらは実装可能性を保つデータ欄を用意しても、確認前に対応済みとは扱わない。

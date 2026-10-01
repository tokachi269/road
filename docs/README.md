# 文書案内

> 文書種別: 入口

このページを文書の入口とする。仕様の正しさは文書だけでは保証しない。
実装コード、テスト、lint、実ゲームでの観測結果を根拠にする。

## 正本の優先順位

判断が食い違う場合は、次の順で確認する。

1. 実行可能なテストとハーネス
2. Decision ownerとして指定された実装コードとデータ
3. [現在の構成と責務](architecture.md)
4. 個別の仕様資料と運用手順
5. 比較検討、作業表、引き継ぎ記録

文書にしかない規則は、実装済みとはみなさない。未検証の説明も確定仕様にしない。
ただし、文書と実装の不一致を見つけた場合は、文書を無視せず差異として扱う。

## 最初に読む文書

実装前は次の2文書を読む。

- [現在の構成と責務](architecture.md)
- [検証方針](testing.md)

agent支援で設計や実装を行う場合は、
[Agent Engineering Harness](engineering/agent_harness.md)も読む。

## 文書一覧

| 文書 | 種別 | 用途 |
| --- | --- | --- |
| [現在の構成と責務](architecture.md) | 現行構成 | owner、依存方向、実装境界を確認する |
| [検証方針](testing.md) | 検証 | 変更に必要な証拠とコマンドを選ぶ |
| [CS1道路生成仕様](cs1-road-requirements.md) | 仕様資料 | CS1、RoadImporter、プロジェクト判断を区別して調べる |
| [Runtime preview](runtime-preview.md) | 運用手順 | 通常マップ向けpreviewの入出力と制約を調べる |
| [設計判断](design-decisions.md) | 比較検討 | 採用候補、不採用案、実証待ちを確認する |
| [実装詳細](reference/implementation-details.md) | 詳細資料 | UV、geometry、Runtimeの現在の詳細を調べる |
| [Runtime preview作業表](runtime-workboard.md) | 状態記録 | 過去の進捗と未検証事項を確認する |
| [これまでの経緯](handoff.md) | 履歴 | 過去の要求、試作、未確定論点を追跡する |
| [Agent Engineering Harness](engineering/agent_harness.md) | 作業手順 | agent作業の証拠、停止条件、独立検証を確認する |

## 更新規則

- 仕様変更は、先にDecision ownerと検証方法を決める。
- 現行契約を変えた場合は、対応するテストまたはハーネスも更新する。
- 調査結果や候補案は、現行構成へ混ぜず比較検討か履歴へ記録する。
- 古い情報は削除せず、履歴へ移して現行文書からリンクする。
- 文書一覧は`tools/arch_lint.py`で検査する。

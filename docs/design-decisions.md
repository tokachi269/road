# 設計判断

この文書は、判断理由と未確定事項を記録します。
実装済み契約の一覧ではありません。

## 採用した方針

### laneを編集入力として残す

laneは交通、接続、transitionのidentityに必要です。
meshのface列はlaneから導出しますが、lane自体は削除しません。

### stable lane IDを使う

laneを追加しても、無関係なboundary設定を維持するためです。
boundary IDは隣接するlane IDから導出します。

### 断面幅を一か所で計算する

UI、mesh、Runtimeが別々に幅を計算すると結果がずれます。
断面の導出は`domain.py`へ集約します。

### variantの直積を作らない

生成器が表現できる組合せと、公開する道路の種類を分けます。
実際に出力する道路はrecipeまたはbuild manifestで選びます。

### Adaptive Roadsを必須にしない

基本の幅、lane、mesh、接続は単独で成立させます。
固有flagは、対応adapterがある場合だけ利用します。

## 採用しない方針

現行schema v3へ重複項目を追加し続けません。
`surface`、`structure`、`tunnel`だけでCS1 entryを決めません。
共通atlasを使うだけで、複数CRP間のruntime共有が成立したとは扱いません。

## 未確定

- 最終schemaでlaneとstripをどう分離するか
- CS1のsegmentとnodeを何entryへ分けるか
- LODをどの規則で生成するか
- 全道路を1つのtexture setで公開するか
- Adaptive Roads固有flagをどこまで扱うか

未確定事項をlintで固定しません。
決定するときはowner、consumer、失敗時の扱い、検証方法を決めます。

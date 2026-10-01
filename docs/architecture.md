# 構成と責務

この文書は、データがどこで決まり、どこへ流れるかを説明します。
個別の寸法やUI操作は扱いません。

## データの流れ

```text
道路定義
  ↓
Blender非依存の導出
  ↓
geometry plan
  ↓
Blender mesh
  ├─ RoadImporter向け出力
  └─ Runtime preview bundle
```

下流の処理は、laneの隣接関係や断面幅を再計算しません。
上流で確定した結果を受け取ります。

## 責務

| 対象 | owner | 役割 |
| --- | --- | --- |
| laneと共有断面 | 道路定義 | 人が編集する入力を保持する |
| boundary、路肩、駐車lane | `road_builder/domain.py` | 入力から道路構造を導出する |
| 主桁配置 | `geometry_plan.py` | Blenderに依存せず配置を決める |
| atlas領域 | `textures/dimensions.json` | UVと画像配置を定義する |
| mesh | Blender add-on | planをBlenderデータへ変換する |
| CS1 entry | RoadImporter、RuntimeHost | CS1の型へ変換する |

Blender UIとJSONは編集用の窓口です。
domainと異なる規則を持たせません。

## 道路定義と派生データ

人が編集する値と、自動計算する値を分けます。

人が編集する主な値は、lane、歩道間幅、歩道幅、分離帯、道路線です。
路肩幅、boundary、lane位置、mesh、UVは自動計算します。

現行schema v3には、laneとstripの重複が残っています。
新しい機能で重複を増やさず、最終schemaとも扱いません。

## geometryとCS1 entry

`surface`、`structure`、`tunnel`はgeometryを整理する名称です。
CS1のentry数を直接決める分類ではありません。

entryはselector、shader、texture setを含めて決めます。
最終構成はAsset Editorと実ゲームで確認してから固定します。

## Runtime preview

RuntimeHostはBlenderが生成したmeshを受け取ります。
道路断面の再生成は行いません。

IMTで作成した線のownerはIMTです。
RuntimeHostは初期値だけを設定し、ユーザ編集を定期的に上書きしません。

## 詳細を探す

具体的な規則と検証先は[契約と検証の対応](contract-map.md)を参照してください。
操作方法は[文書案内](README.md)から作業別文書を選んでください。

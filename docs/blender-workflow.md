# Blenderでの作業

この文書は、道路を編集し、previewを作る手順を説明します。

## add-onを読み込む

```powershell
.\scripts\install-blender-addon.ps1
```

Blenderの3D Viewで`N`キーを押し、`Road`タブを開きます。
コード変更後は`Development > Reload Scripts`を押してください。
再読込後はpreviewを再生成してください。

## 道路を編集する

道路全体の断面、lane、分離帯、道路線を上から順に編集します。
modeはタブで切り替えます。

入力値が歩道間幅に収まらない場合、add-onは寸法を縮めず警告を表示します。
警告中もpreviewは生成できます。

`Build active mode`は選択中modeだけを作ります。
`Build all modes`は全modeを作り、高さを16 mずつずらして表示します。

## 生成結果を編集する

各modeにはsegmentとnodeの2 objectがあります。
生成後のobjectは通常のBlender meshとして編集できます。

同じmodeを再生成すると、そのmodeの生成物を置き換えます。
直接編集した内容を残したい場合は、再生成前に別objectへ保存してください。

## カスタムElevated端部

`Edge mesh (right basis)`には右側用のMesh Objectを指定します。
add-onは左側をX反転し、法線を保つよう頂点順も反転します。

Object原点は右路面外角に合わせます。
形状はY方向へ作り、路面より上をZ正、床版側をZ負に配置します。

カーブ変形させない頂点は`CS1_NO_SPLIT` vertex groupへ入れます。
接続先と重なる長手端のcapは出力しません。

この入力契約はBlender smokeで検査します。
見た目と意図した法線はviewportでも確認してください。

## JSON

JSONは編集内容を保存しますが、派生値のownerではありません。
schema v3にはlaneとstripの重複が残っています。
新しい項目を追加するときは[構成と責務](architecture.md)を先に確認してください。

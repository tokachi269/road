# テクスチャ制作

この文書は、共通atlasを制作してBlenderとRuntimeへ渡す手順を説明します。

## owner

atlas寸法、region、UV profile、map名は`textures/dimensions.json`が定義します。
PSDとPNGの位置を見てUV座標を手入力しないでください。

固定pixel値は`tests/texture_layout_test.py`でも検査します。
manifestと生成コードが同時にずれても、test側の期待値で検出します。

## Photoshopから出力する

画像の出力にはPhotoshop GeneratorのImage Assetsを使います。
PSDのgroup名は`<family>_<map>.png`にします。

familyは`surface`、`structure`、`tunnel`です。
mapは`d`、`a`、`p`、`r`、`n`、`s`です。

必須mapは`d`だけです。出力先はPSD横の`road-assets`です。
すべての画像は透明部分を含むatlas全体の寸法で出力してください。

## 検証する

```powershell
python tools/validate_texture_layout.py
python -m unittest tests.texture_layout_test
```

検証では、画像寸法、region重複、padding、縮小時の整合を確認します。
lineの管理slotと、faceへ割り当てる実内容幅も区別します。

## Blenderで確認する

Blenderでは`d`、`a`、`n`、`s`をmaterialへ接続します。
`p`と`r`は読み込みますが、theme textureがないため見た目へ合成しません。

Generator出力を更新すると、add-onが画像の変更を検出します。
手動更新には`Reload Photoshop Generator PNGs`を使います。

## Runtimeへの変換

Runtimeでは`d`を`_MainTex`へ渡します。
`a`、`p`、`r`は`_APRMap`へまとめます。
`n`と`s`は`_XYSMap`へまとめます。

channelの詳細と欠損時の既定値はRuntime contractで検査します。
文書へ同じ数値を複写しません。

# 共通アイコンレイヤー

`overlay.png`を128×128の透過PNGで保存する。この1枚を全ベース道路のアイコンに重ねる。
背景を透過にし、車線矢印や下部ラベルを隠さないように描く。半透明も使用できる。

白紙の作業元は`build/runtime-preview/icons/overlay-template.png`。
アイコンの本体は自動描画されるため、このレイヤーへ車線数・高さ・幅を描き込まない。

保存したらリポジトリrootから実行する。

```powershell
.\scripts\generate-road-icons.ps1
.\scripts\publish-runtime-hot.ps1
```

生成PNGは`build/runtime-preview/icons`へ出力される。別のpreview directoryを使用する場合は
生成コマンドへ`-PreviewPath`を指定する。生成コマンドはHostの`preview.path`を変更しない。
`overlay.png`は手描きの制作元で、生成コマンドでは上書きしない。

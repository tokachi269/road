# Road repository working contract

実装前に`docs/architecture.md`と`docs/testing.md`を読む。`docs/design-decisions.md`は比較評価であり、確定contractとして扱わない。

- 正本と派生物を増やす変更では、先にDecision ownerを明示する。
- Blender UI、JSON、mesh生成で同じ判断を再実装しない。
- Blender非依存の判断は`blender_addon/road_builder/domain.py`へ置き、`bpy`を持ち込まない。
- RoadImporterは出力consumerであり、道路定義の正本にしない。
- 現在のschema v3を確定仕様とみなさない。特に`lanes`と`layout.strips`の重複を拡張しない。
- 未決定のmesh分割、UV、atlas、recipe形式をarchitecture lintへ入れない。
- 新しい抽象化は、実在する道路variantまたはCS1 import契約が必要になるまで追加しない。
- production変更後は最低限、`python tools/arch_lint.py`と`python -m unittest tests.domain_contract_test tests.geometry_plan_test tests.architecture_harness_test`を実行する。
- Blender geometryまたはUIを変えた場合は`tests/blender_addon_smoke.py`もBlender 5.1 backgroundで実行する。
- commit、push、ゲーム環境へのinstallは明示依頼がある場合だけ行う。

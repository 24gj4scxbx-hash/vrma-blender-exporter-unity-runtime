# VRMA Tools

**VRMAの汎用化** — VRM 1.0エコシステムに限定されていたVRMAを、VRM 0.x、FBX、多様なリグへ拡張するツール群。

🌐 [English](README.md) | [한국어](README_ko.md)

---

## このプロジェクトについて

VRMA（VRM Animation）はVRM 1.0専用のモーションフォーマットです。しかし、現実のVRMエコシステムは大半が0.xであり、FBXモデルも混在し、公式ツールはbone rotationのみを扱います。

本プロジェクトは3つの独立したツールで構成されています：

| ツール | 役割 |
|--------|------|
| **blender_vrma_exporter** | BlenderからVRM/FBX/カスタムリグ → VRMAエクスポート |
| **Unity_MotionController** | Unityで外部ストレージからVRMAをランタイムロード＋再生 |
| **glTF_mesh_separator** | glTF/VRMのマルチサブメッシュを独立メッシュに自動分離 |

---

## blender_vrma_exporter (v8.0)

Blenderから様々なヒューマノイドリグをVRMAにエクスポートする汎用エクスポーター。

### 公式VRMアドオンがカバーしない領域

公式VRM Add-on for BlenderのVRMAエクスポートは、VRM 1.0モデルのbone rotationを対象に動作します。blender_vrma_exporterは、公式が扱わない領域をカバーします：

| シナリオ | 公式VRMアドオン | blender_vrma_exporter v8.0 |
|----------|----------------|---------------------------|
| VRMモデル（0.x / 1.0 バージョン不問） | 1.0のみ | ✅ |
| FBXモデル（Mixamo等）からモーション制作 → VRMA | — | ✅ ⚠️ |
| 任意のボーン命名規則（カスタムリグ） | — | ✅ ワイルドカードマッチング ⚠️ |
| Expression（Shape Key）同時エクスポート | — | ✅（bone + expression） |
| T-pose自動保証 | — | ✅ |
| 既存VRMAインポート → 編集 → 再エクスポート | — | ✅ ⚠️ |

> ⚠️ マークの項目は技術的に可能であることを示しています。これらの機能の使用により発生する法的・著作権上の問題はユーザーの責任であり、本ツールの技術的機能に起因するものではありません。モーションデータやモデルのライセンスを必ず確認した上でご利用ください。

### 主要機能

- **ワイルドカードボーンマッチング**: `mixamorig:Hips`、`J_Bip_C_Hips`、`Character1_Hips` — プレフィックスと区切り文字（`:`, `_`, `.`, `-`）を自動検出し、様々な命名規則に対応
- **Expression同時エクスポート**: Shape Key値の変化を自動検出し、bone rotationと共にVRMAに含めます。VRM preset/custom自動分類
- **T-pose自動保証**: 実行時にframe 0でClear Transform + キーフレーム自動挿入
- **固定非ゼロ表情認識**: 値が変化しなくても、非ゼロ値があればアクティブとして認識（例：固定表情）
- **ログ出力**: `.vrma`の隣に`.log.txt`を自動生成。リグタイプ、マッチしたボーン、アクティブなShape Key等を記録

### VRMA編集ワークフロー

既存のVRMAファイルをBlenderで編集し、再エクスポートできます：

1. 公式VRMアドオンでVRMAインポート（File → Import → VRM Animation）
2. モデルに適用されたモーションをBlenderで確認
3. ポーズ修正 / 表情追加 / タイミング調整
4. blender_vrma_exporterで再エクスポート

### 使い方

1. Blenderでモデルを開く（VRM、FBX、またはBlenderネイティブモデル）
2. モーション＋表情キーフレーム作業
3. ファイル先頭の`OUTPUT_DIR`を希望の出力パスに変更
4. Scriptingタブ → スクリプト貼り付け → ▶ 実行
5. 出力フォルダに`.vrma` + `.log.txt`が生成

### 要件

- Blender 4.x以上
- 外部依存なし（Blender内蔵Pythonのみ使用）

---

## Unity_MotionController

外部ストレージからVRMAファイルをランタイムでロード・再生するUnityコンポーネント。ビルド内モーションアセット0 — パスを指定するだけで、任意のVRMAを即座にロードして再生します。

### 独自エンジン

UniVRMはVRMAのbone animationを再生できますが、expression（表情）のランタイム直接制御は困難です。MotionControllerは2つの独自エンジンでこれを解決します：

- **World Retarget**: bone animationをWORLD rotation deltaで計算し、任意のVRMモデルにリターゲティング
- **Direct Binary Parser**: VRMAファイルのGLBバイナリを直接パースし、expressionキーフレームを抽出、時間補間後SetBlendShapeWeightで適用

### 主要機能

- **Bone + Expression同時再生**: VRMAに含まれるbone rotationとexpression weightを同時に適用
- **VRMバージョン不問**: VRM 0.xモデルでもexpressionが動作。BlendShape名ベースの直接マッチングでVRMスペックバージョンに依存しません
- **preset/custom両対応**: VRMAのpreset expressionとcustom expressionを同一に処理

### アーキテクチャ

```
MotionController
  LateUpdate:
    1. Bone: curr × Inv(rest) → WORLD delta → Inv(parentDelta) × delta → local rotation
    2. Expression: binary search + Lerp → SetBlendShapeWeight(index, weight × 100)
```

### 要件

- Unity 2021.3以上
- UniVRM（VRMモデルロード用）

---

## glTF_mesh_separator

glTF/VRMファイルのマルチサブメッシュ（multi-primitive mesh）を独立メッシュに自動分離するツール。

### なぜ必要か

VRoid Studio等のツールは、異なるマテリアルのパーツを1つのメッシュにサブメッシュとして結合する場合があります。この構造は環境によって以下の問題を引き起こす可能性があります：

- シェーダーによってはGPU深度ソートの問題が発生（z-fighting）
- サブメッシュ単位のon/off不可 → 衣装・小物の個別トグルが困難
- BlendShapeが不要なサブメッシュにも全適用 → 不要な演算

### 主要機能

- **自動分離**: 1メッシュNサブメッシュ → N個の独立メッシュ
- **BlendShape完全保存**: vertex index remap + BlendShapeリマッピング
- **BoneWeight/UV/Normal保存**: 全vertex attribute維持
- **VRM BlendShapeGroup参照更新**: 分離されたメッシュ全てに自動拡張
- **分析モード**: `--analyze`オプションで分離対象のみ事前確認

### 使い方

**ドラッグ＆ドロップ:**
`.vrm`または`.glb`ファイルを`glTF_mesh_separator.bat`にドラッグ

**コマンドライン:**
```bash
python glTF_mesh_separator.py input.vrm                    # 自動出力名
python glTF_mesh_separator.py input.vrm output.vrm         # 出力名指定
python glTF_mesh_separator.py input.vrm --analyze          # 分析のみ（分離なし）
```

### 要件

- Python 3.x
- 外部依存なし（標準ライブラリのみ使用）

---

## 検証状況

| テスト | 結果 |
|--------|------|
| blender_vrma_exporter: VRMモデル → VRMA | ✅ 54/54 bone、expression正常 |
| blender_vrma_exporter: FBXモデル → VRMA | ✅ 54/54 bone、expression正常 |
| blender_vrma_exporter: VRMAインポート → 編集 → 再エクスポート | ✅ モーション＋表情保存 |
| Unity_MotionController: VRM 0.x + VRMA expression | ✅ preset + custom動作 |
| Unity_MotionController: bone + expression同時再生 | ✅ |
| glTF_mesh_separator: VRM 4種バッチ分離 | ✅ 4/4成功、BlendShape保存 |

---

## 免責事項

これらのツールは技術的機能を提供するものであり、使用により発生する法的・著作権上の問題に対する責任はユーザーにあります。モーションデータ、3Dモデル、その他アセットのライセンスおよび利用条件を必ず確認した上でご利用ください。

---

## ライセンス

AGPL-3.0

これらのツールを修正して配布する場合、ソースコードの公開義務があります。

---

## コントリビューション

バグレポート、機能提案、Pull Requestを歓迎します。

- blender_vrma_exporterのBlenderアドオン化
- 追加リグ命名規則のサポート
- 他エンジン（Unreal、Godot）向けランタイムプレイヤー

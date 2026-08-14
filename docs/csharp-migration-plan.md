# wip C# / Native AOT 移行計画

目標パイプライン:

```
C# → Native AOT → wip.exe → ZIP → GitHub Releases → WinGet
```

本ドキュメントは移行の「計画」であり、実装は含まない。合意した時点でフェーズ 0 から着手する。

---

## 1. 現状の棚卸し

| 項目 | 現状 |
|---|---|
| 実装 | Ruby 3.2+ / Thor 1.3 |
| 本体規模 | `lib/` + `exe/` で 23 ファイル・約 3,000 行 |
| テスト | RSpec 17 ファイル・約 3,400 行 |
| 配布 | RubyGems (`wslc-wip`) + GitHub Packages |
| リリース | release-drafter → `gem-push.yml` (workflow_run 連鎖) |
| 実行環境 | **WSL2 内の Linux が主**。`wslc.exe` を interop 経由で呼ぶ。ネイティブ Windows も一応サポート |

依存は Thor のみ。外部 API を叩かず、YAML/JSON のパースとプロセス起動が仕事の大半という、AOT 化に向いた形をしている。

### 移行の動機（確認）

- **Ruby ランタイム要求の撤廃**: 現状 `gem install wslc-wip` には Ruby 3.2+ が要る。WSLC を使う Windows 開発者に Ruby を強制するのは導線として重い。
- **起動速度**: Ruby + Thor の起動は 200–400ms 程度。Native AOT なら 10–30ms。`wip exec` を 1 日に何十回も叩くツールとして体感差が出る。
- **配布**: WinGet で `winget install slidict.wip` の 1 行にできる。

---

## 2. 最初に決めるべきこと

実装より先に確定が必要な項目。**推奨案**を併記する。

### 決定 1: ターゲット RID — Windows のみか、Linux も出すか 🔴最重要

`wip.exe` という表現から Windows 向けは確定だが、**現在の wip は WSL2 の Linux 側で動くのが主用途**である。README の要件も「WSL2 内で動かす」前提で書かれており、`Environment#wsl2?` は `/proc/version` を読み、`BuildContext` の shadow 機構は「WSL から `/mnt` 越しにビルドコンテキストを送ると遅い」という Linux 側固有の問題への対策になっている。

つまり Windows 専用にすると、これは移植ではなく**動作場所の変更**になる。影響:

- `wip.yml` に書くパスが Linux パス → Windows パスに変わる（既存ユーザーの設定が壊れる）
- `sync:` の rsync ミラーリング、`shadow_context` の存在意義が変わる（Windows 側から直接叩くなら shadow は不要になる）
- 逆に「WSL 内でソースを編集し、WSL 内のシェルから `wip up` を叩く」という現行ワークフローが失われる

| 選択肢 | 内容 | 評価 |
|---|---|---|
| **A（推奨）** | win-x64 / win-arm64 / linux-x64 / linux-arm64 の 4 RID を出す。WinGet は Windows 分のみ、Linux は tar.gz を Releases に置く | 現行ユーザーを切らずに WinGet 導線を足せる。CI マトリクスが増えるだけでコストは小さい |
| B | Windows のみ | 実装は最小。ただし現行ユーザーの移行パスが無く、`shadow_context` / `sync` 周りの設計を作り直すことになる |
| C | Linux のみ | WinGet の話が成立しないので却下 |

**推奨: A。** Native AOT は 1 バイナリ per RID なので、マトリクスビルドで機械的に増やせる。

### 決定 2: Ruby gem をどうするか

| 選択肢 | 内容 |
|---|---|
| **A（推奨）** | C# 版がパリティ到達するまで gem を維持 → 到達後、最終版を出して deprecation 告知（`gem deprecate` 相当の説明を README/gemspec に）→ 以後凍結 |
| B | 即座に gem を停止 | 既存ユーザーが移行先を持たないまま切られる |
| C | 恒久的に両方メンテ | 現実的でない（2 実装の仕様ドリフト） |

**推奨: A。** 移行期間中は golden テスト（§5）で両実装の出力一致を担保する。

### 決定 3: リポジトリを分けるか

**推奨: 同一リポジトリ (`slidict/wip`) 内に併存させる。** Wiki・Issues・Stars・Releases の履歴を引き継げる。Ruby 側は `legacy/` に退避せず、そのまま残して Phase 5 で削除する（履歴は git に残る）。

### 決定 4: CLI フレームワーク

| 候補 | AOT 適性 | 備考 |
|---|---|---|
| **System.CommandLine 2.0（推奨）** | ◎ 公式に AOT 対応を掲げている | Microsoft 製。Thor 相当の機能（サブコマンド、グローバルオプション、help 生成）が揃う |
| Spectre.Console.Cli | △ | コマンド型の解決にリフレクションを使う箇所があり、AOT では警告・トリム対応が必要 |
| 自前パーサ | ◎ | 依存ゼロで最小サイズだが、help 生成とエラーメッセージを全部書くことになる |

**推奨: System.CommandLine 2.0。** ただし着手時に最新版の AOT 対応状況とパッケージバージョンを確認すること（§9）。

`Spectre.Console`（Cli ではなく描画部分のみ）は `StagingProgress` の進捗表示に使う選択肢があるが、現状の実装は単純な stderr 出力なので、まずは依存を増やさず自前で書く。

### 決定 5: コード署名

Native AOT の未署名 exe は Windows Defender SmartScreen の警告対象になりうる。ZIP + portable 配布は MSI インストーラより警告に当たりにくいが、実行ファイル自体のレピュテーションはゼロから積むことになる。

| 選択肢 | コスト | 備考 |
|---|---|---|
| **A（推奨・当面）** | 0 | 署名なしで出す。WinGet 経由なら winget-pkgs 側の検証を通っているという担保はある |
| B | Azure Trusted Signing（月額・本人/組織確認あり） | 将来的な選択肢。導入は CI に署名ステップを 1 つ足すだけ |

**推奨: A で開始し、警告の実害が出たら B を検討。** 決定 1〜4 と違い、後から足せるので初期の障害にはしない。

---

## 3. リポジトリ構成

```
wip/
├── Directory.Build.props        # net10.0 / LangVersion / 共通 AOT 設定 / Version
├── Directory.Packages.props     # Central Package Management (バージョン一元管理)
├── wip.slnx
├── src/
│   ├── Wip.Core/                # ロジック（AOT 制約は守るが library 自体は通常ビルド）
│   │   ├── Configuration/       # Config, ConfigLoader, DotenvLoader, SyncSettings
│   │   ├── Compose/             # ComposeFile, ComposeBridge, VariableInterpolation
│   │   ├── Build/               # BuildContext, DockerIgnore, StagingProgress
│   │   ├── Execution/           # CommandBuilder, CommandRunner, CommandResolver, CommandDisplay
│   │   ├── Diagnostics/         # Doctor, ErrorInterpreter, DebugReporter, ResourceMonitor
│   │   └── Platform/            # Environment, Shellwords, Interop (P/Invoke)
│   └── Wip.Cli/                 # PublishAot=true。エントリポイントとコマンド定義のみ
├── tests/
│   ├── Wip.Tests/               # xUnit（通常の CoreCLR で実行）
│   └── golden/                  # Ruby / C# 双方が読む移行パリティ用フィクスチャ
├── lib/ exe/ spec/              # Ruby 実装（Phase 5 で削除）
└── packaging/winget/            # WinGet マニフェストのテンプレート
```

`Wip.Core` と `Wip.Cli` を分けるのは**テスト容易性のため**。Native AOT 発行は `Wip.Cli` にのみ適用され、`Wip.Core` は xUnit から通常参照できる。ただし `Wip.Core` にも `<IsAotCompatible>true</IsAotCompatible>` を立て、リフレクション依存をコンパイル時に検出させる。

### 共通ビルド設定（`Directory.Build.props` の要点）

```xml
<TargetFramework>net10.0</TargetFramework>
<Nullable>enable</Nullable>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<InvariantGlobalization>true</InvariantGlobalization>   <!-- ICU を落としてサイズ削減 -->
<UseSystemResourceKeys>true</UseSystemResourceKeys>      <!-- 例外メッセージリソースを落とす -->
<PublishAot>true</PublishAot>                            <!-- Wip.Cli のみ -->
<StripSymbols>true</StripSymbols>
<IlcOptimizationPreference>Size</IlcOptimizationPreference>
```

`InvariantGlobalization=true` は妥当か要確認（§9）。`ErrorInterpreter` の正規表現マッチや `SECRET_PATTERN` の大文字小文字無視比較は、明示的に `StringComparison.OrdinalIgnoreCase` を使えば問題ない。

---

## 4. モジュール別 移植計画

「難度」は AOT 制約と .NET BCL とのギャップの大きさ。

| Ruby | 行数 | C# 移植先 | 難度 | 要点 |
|---|---:|---|:---:|---|
| `version.rb` | 5 | `Directory.Build.props` の `<Version>` | 易 | 単一の版元をここに移す |
| `errors.rb` | 7 | `WipException` 階層 | 易 | |
| `command_display.rb` | 19 | `CommandDisplay` | 易 | |
| `dotenv_loader.rb` | 39 | `DotenvLoader` | 易 | 正規表現をそのまま移植 |
| `environment.rb` | 43 | `Platform/Environment` | 中 | `RbConfig[host_cpu]` → `RuntimeInformation.OSArchitecture`。`Gem.win_platform?` → `OperatingSystem.IsWindows()`。`$stdin.tty?` → `!Console.IsInputRedirected` |
| `command_resolver.rb` | 48 | `CommandResolver` | 易 | `File.executable?` → Windows は PATHEXT、Unix は `UnixFileMode` の x ビット確認が要る |
| `variable_interpolation.rb` | 60 | `VariableInterpolation` | 易 | |
| `staging_progress.rb` | 63 | `StagingProgress` | 易 | |
| `debug_reporter.rb` | 66 | `DebugReporter` | 易 | |
| `compose_bridge.rb` | 72 | `ComposeBridge` | 易 | |
| `docker_ignore.rb` | 75 | `DockerIgnore` | 中 | glob マッチを自前実装。**振る舞い一致が重要**（golden テストで担保） |
| `error_interpreter.rb` | 90 | `ErrorInterpreter` | 易 | 正規表現を `[GeneratedRegex]` に。AOT 相性◎ |
| `resource_monitor.rb` | 94 | `ResourceMonitor` | 中 | |
| `sync_settings.rb` | 157 | `SyncSettings` | 中 | |
| `doctor.rb` | 158 | `Doctor` | 中 | `Data.define` → `record` |
| `initializer.rb` | 220 | `Initializer` | 中 | テンプレート文字列は raw string literal (`"""`) が使える |
| `build_context.rb` | 219 | `BuildContext` | **難** | ファイル排他ロック・シンボリックリンク・Unix パーミッション保持・アトミック rename。§5 参照 |
| `command_builder.rb` | 234 | `CommandBuilder` | 中 | ロジックは素直だが `Shellwords.split` が要る（§5） |
| `config.rb` | 273 | `Config` | 中 | YAML を「文字列キーの辞書」として扱う設計が AOT と相性◎（§5） |
| `compose_file.rb` | 272 | `ComposeFile` | 中 | 同上 |
| `command_runner.rb` | 205 | `CommandRunner` | **最難** | PTY。§5 参照 |
| `cli.rb` | 541 | `Wip.Cli` | 中 | System.CommandLine への読み替え。`reorder_global_options` と `dispatch` フォールバックが独自仕様（§5） |

---

## 5. Native AOT 固有の技術課題

移行の成否を左右するのはここ。**フェーズ 0 で個別に技術検証（スパイク）を行う。**

### 5.1 YAML パース 🔴

AOT 最大の地雷は「型へのマッピングにリフレクションを使うシリアライザ」。だが幸い、現行 Ruby 実装は YAML を **プレーンな Hash として扱っている**（`Config#stringify` が全キーを文字列化し、以降 `@raw['dependencies']` のように辞書アクセスするだけ）。

したがって **表現モデル（ノードツリー）としてパースする**方針を取れば、リフレクションは一切発生しない:

- `YamlDotNet` の `YamlStream` / `YamlMappingNode` を使う → デシリアライザを通さないので AOT 安全
- もしくは `YamlDotNet.Analyzers.StaticGenerator` でソース生成コンテキストを使う

**推奨: 表現モデル方式。** Ruby 実装のコード形状をほぼそのまま写せるので、移植時のバグ混入も減る。`YamlDotNet` を AOT で発行できることはフェーズ 0 で実測する。

なお `ConfigLoader` は既に `YAML.safe_load_file(permitted_classes: [], aliases: false)` を使っており、**アンカー/エイリアスを禁止済み**。この制約は C# 側でも維持する（実装が単純になる）。

### 5.2 JSON パース

`wslc list --format json` の出力を読むだけ。`System.Text.Json` の `JsonDocument` は**リフレクション不使用**でそのまま AOT 動作する。POCO へのデシリアライズはしない（現行 Ruby も `entry['State']` のような辞書アクセスのみ）。

### 5.3 プロセス起動と PTY 🔴最難

現行の `CommandRunner` は 3 経路を持つ:

1. `run` — パイプ。stdout/stderr を吸って `ErrorInterpreter` に渡す
2. `run_attached` — Linux。**openpty(3) 越しに起動**し、raw モード + SIGWINCH 追従 + 出力キャプチャを両立
3. `run_inherited` — Windows。stdio を素通し（キャプチャは諦める）

.NET には PTY の BCL API が無い。かつ `Process` クラスは子プロセスに制御端末を割り当てる手段を提供しない。

| 選択肢 | 内容 | 評価 |
|---|---|---|
| **A（推奨・Phase 2）** | 対話コマンドは **stdio 継承**に統一（`ProcessStartInfo.RedirectStandard* = false`）。両 OS で同一実装 | ジョブ制御・Ctrl-C・isatty 判定は正しく動く。**代償: 対話コマンドで `ErrorInterpreter` のヒントが出せなくなる**（Windows では現状も出ていないので、Linux 側のみの機能後退） |
| B（将来） | Windows は ConPTY (`CreatePseudoConsole`)、Linux は `forkpty` を P/Invoke | 現行パリティを完全維持できるが、実装量とプラットフォーム別デバッグが大きい |

**推奨: A で出し、対話時のエラーヒントが実際に惜しまれたら B を追加。** 非対話経路（`probe`、`resource_exists?`、通常の `execute`）はキャプチャを維持するので、`wip up` / `wip doctor` のヒントは従来どおり出る。

引数配列渡し（シェル解釈なし）という wip の設計上重要な性質は、`ProcessStartInfo.ArgumentList` でそのまま保てる。

### 5.4 `Shellwords.split` 相当

`command:` の文字列を argv に割るのに POSIX shell 準拠の分割が要る（`CommandBuilder#custom`、`CLI#dispatch_compose`、`up` など計 4 箇所）。BCL に相当機能は無いので**自前実装（50 行程度）**。golden テストで Ruby の `Shellwords.split` と全ケース突き合わせる。

### 5.5 `BuildContext` のファイル操作

| Ruby | C# | 備考 |
|---|---|---|
| `lock.flock(File::LOCK_EX)` | Windows: `FileStream.Lock` / Unix: `flock(2)` を P/Invoke | .NET の `FileShare` は Unix では強制されない。**P/Invoke が必要** |
| `File.rename`（アトミック置換） | `File.Move(src, dst, overwrite: true)` | Unix では rename(2) 相当。Windows も置換可 |
| `FileUtils.copy_entry(..., preserve)` | `File.Copy` + `File.SetUnixFileMode` | 実行ビット保持は .NET 7+ の `UnixFileMode` API で可能 |
| シンボリックリンクを解決せずコピー | `FileSystemInfo.LinkTarget` + `File.CreateSymbolicLink` | **セキュリティ上重要**（現行コメント参照: `~/.ssh/id_rsa` の混入防止） |
| `Find.prune` によるツリー枝刈り | `Directory.EnumerateFileSystemEntries` の手動再帰 | `EnumerationOptions.RecurseSubdirectories` では枝刈りできないので手書きの再帰にする |
| `stat.mtime.nsec` | `File.GetLastWriteTimeUtc().Ticks`（100ns 精度） | **精度が異なる**。manifest フォーマットが変わるので、初回実行時にシャドウを 1 回再構築させる（manifest バージョンを入れて判定） |

### 5.6 CLI 表層の独自仕様

`cli.rb` には Thor の制約を回避するための独自ロジックが 2 つある。System.CommandLine には Thor と別の癖があるので、**そのまま移植するのではなく再設計**する:

1. `reorder_global_options` — `wip --config foo up` を `wip up --config foo` に並べ替える。System.CommandLine のグローバルオプションは元々位置に依存しないため、**この処理ごと不要になる可能性が高い**（要検証）。
2. `dispatch` フォールバック — 未知のコマンド名を `wip.yml` の `commands:` エントリとして解決する。System.CommandLine では未マッチ時のハンドラを自前で挿す必要がある。**ここは要スパイク。**

### 5.7 バイナリサイズと起動時間の目標

| 指標 | 目標 |
|---|---|
| `wip.exe` サイズ | 圧縮前 < 10 MB / ZIP 後 < 5 MB |
| `wip version` 実行時間 | < 30 ms |
| ZIP 内容 | `wip.exe` 単体（PDB は別アセットか同梱しない） |

フェーズ 0 のスパイクで YamlDotNet + System.CommandLine を入れた状態のサイズを実測し、目標を超えるなら依存を見直す。

---

## 6. 配布パイプライン

### 6.1 全体像

```
git tag v1.2.0
      │
      ▼
┌─ build.yml (matrix) ─────────────────────────────┐
│  windows-latest    → win-x64   → wip.exe         │
│  windows-11-arm    → win-arm64 → wip.exe         │
│  ubuntu-latest     → linux-x64 → wip             │
│  ubuntu-24.04-arm  → linux-arm64 → wip           │
│         各 RID を ZIP / tar.gz 化 + SHA256        │
└──────────────────────┬───────────────────────────┘
                       ▼
            GitHub Release（release-drafter の下書きを publish）
                       │  on: release published
                       ▼
            winget.yml → microsoft/winget-pkgs へ PR 自動作成
```

**重要: Native AOT はクロス OS ビルドができない。** win-x64 バイナリは Windows ランナー上でしか作れないので、マトリクスは OS 別ランナーが必須。

### 6.2 リリーストリガの変更

現行は `Changelog` ワークフローの成功 → `workflow_run` で `gem-push` という連鎖になっている。これは追いにくいので、**タグ駆動に単純化**することを提案する:

- `git tag v1.2.0 && git push --tags` → `release.yml` が起動
- バージョンの単一の版元は `Directory.Build.props` の `<Version>`。CI がタグとの一致を検証して不一致なら fail
- 既存の `bump-version.yml` は `version.rb` ではなく `Directory.Build.props` を書き換えるよう改修
- release-drafter によるリリースノート生成はそのまま活かす

### 6.3 成果物の命名

```
wip-1.2.0-win-x64.zip
wip-1.2.0-win-arm64.zip
wip-1.2.0-linux-x64.tar.gz
wip-1.2.0-linux-arm64.tar.gz
SHA256SUMS
```

WinGet のマニフェストは URL にバージョンを埋め込むので、**この命名規則は一度決めたら変えない**。

加えて `actions/attest-build-provenance` でビルド来歴の証明を付ける（Actions の標準機能で、コストゼロ）。

### 6.4 WinGet マニフェスト

`InstallerType: zip` + `NestedInstallerType: portable` を使う。ZIP に exe を 1 つ入れるだけの構成に対応した仕組みで、まさに今回の形に合致する（マニフェストスキーマ 1.6 以降）。

```yaml
# Slidict.Wip.installer.yaml（骨子）
PackageIdentifier: Slidict.Wip
PackageVersion: 1.2.0
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
  - RelativeFilePath: wip.exe
    PortableCommandAlias: wip
Installers:
  - Architecture: x64
    InstallerUrl: https://github.com/slidict/wip/releases/download/v1.2.0/wip-1.2.0-win-x64.zip
    InstallerSha256: <sha256>
  - Architecture: arm64
    InstallerUrl: https://github.com/slidict/wip/releases/download/v1.2.0/wip-1.2.0-win-arm64.zip
    InstallerSha256: <sha256>
ManifestType: installer
ManifestVersion: 1.6.0
```

必要なファイルは 3 点セット（version / installer / locale）。

**準備が要るもの:**

1. **PackageIdentifier の確定** — `Slidict.Wip` を想定。winget-pkgs では publisher 部分が実在の発行者名と対応している必要がある。
2. **microsoft/winget-pkgs のフォーク** — 自動 PR の宛先。
3. **PAT (classic, `public_repo` スコープ)** — リポジトリシークレットに登録。`GITHUB_TOKEN` では他リポジトリに PR を出せない。
4. **自動化アクション** — `vedantmgoyal9/winget-releaser`（zip/portable 対応）または `wingetcreate update` を CI から呼ぶ。

**注意点:**

- 初回投稿は winget-pkgs 側の人手レビューが入るため、マージまで数日かかることがある。**Phase 5 のスケジュールに余裕を持たせる。**
- 検証には published な（draft/prerelease でない）リリースが必要。よって WinGet ジョブは `on: release: types: [published]` にする。
- `portable` パッケージは winget が links ディレクトリに shim を作り PATH に通す。ユーザーは `winget install Slidict.Wip` の後、新しいシェルで `wip` が使える。

### 6.5 Linux 側の配布（決定 1 で A を選んだ場合）

WinGet の対象外なので、当面は Releases の tar.gz を手動 DL + 展開。将来的な選択肢:

- インストールスクリプト（`curl -fsSL https://... | sh`）
- Homebrew tap（Linuxbrew でも動く）
- `.deb` / `.rpm`

**Phase 5 では tar.gz のみとし、それ以上は移行完了後の別課題とする。**

---

## 7. パリティ担保（golden テスト）🔴

2 実装が並存する期間、**仕様ドリフトを機械的に検出する仕組み**を先に作る。これが移行全体の安全網になる。

`tests/golden/` に入出力のペアを置き、Ruby(RSpec) と C#(xUnit) の**両方が同じフィクスチャを読んで同じ結果を主張する**:

```
tests/golden/
  001-container-basic/
    wip.yml
    .env
    cases.json        # [{ "argv": ["up","-d"], "expect": ["wslc","run","--name","app", ...] }, ...]
  002-compose-native/
    wip.yml
    compose.yml
    cases.json
  003-sync-exec/
  ...
```

対象は「入力 → 出力が純粋関数になっている層」に絞る:

- `CommandBuilder` が生成する argv 配列 ← **最重要。ここが一致すれば実行時の振る舞いも一致する**
- `Config#to_h` の正規化結果
- `DotenvLoader` のパース結果
- `DockerIgnore#ignored?` の判定
- `Shellwords.split` の分割結果
- `ComposeFile#to_dependencies_hash` の変換結果
- `Doctor` の判定結果（環境依存部分はモック）

**まず Ruby 側で既存 spec からフィクスチャを抽出し、Ruby がそれに合格することを確認する（フェーズ 1 の最初のタスク）。** 以降 C# 実装はこのフィクスチャを緑にすることをゴールにする。

`CommandRunner` の PTY 挙動や `BuildContext` の実ファイル操作は golden 化できないので、**手動テストマトリクス**（§8）で担保する。

---

## 8. フェーズ分割

### Phase 0 — 決定とパイプライン検証（先にここを通す）

**方針: ロジックを 1 行も書く前に、配布経路を最後まで通す。** 一番リスクが高いのは実装ではなく WinGet までの経路なので、`wip version` だけを返すダミー実装で全部を先に検証する。

- [ ] §2 の決定 1〜5 を確定
- [ ] .NET 10 SDK 前提でソリューション骨格を作成
- [ ] `wip version` だけ返す `Wip.Cli` を Native AOT 発行 → **サイズと起動時間を実測**
- [ ] YamlDotNet 表現モデル / System.CommandLine を入れた状態で AOT 発行が通るか検証（スパイク）
- [ ] 対話プロセス起動（stdio 継承）で Ctrl-C とジョブ制御が期待どおり効くか、Windows / WSL の両方で実測
- [ ] マトリクスビルド → ZIP → プレリリース公開までを CI で通す
- [ ] WinGet マニフェストを手動生成し、`winget validate` / `winget install --manifest` でローカル検証（**PR は出さない**）

**完了条件: ダミー wip.exe が WinGet ローカルマニフェスト経由でインストールでき、`wip version` が動く。**

### Phase 1 — 純粋ロジック層

- [ ] `tests/golden/` のフィクスチャを既存 RSpec から抽出、Ruby 側で緑を確認
- [ ] `Shellwords` / `DotenvLoader` / `DockerIgnore` / `VariableInterpolation` / `ErrorInterpreter`
- [ ] `Config` / `ConfigLoader` / `SyncSettings` / `ComposeFile` / `ComposeBridge`
- [ ] `CommandBuilder`
- [ ] **完了条件: golden テストが C# 側で全緑**

### Phase 2 — 実行・IO 層

- [ ] `Platform/Environment`（WSL2 検出、interop 検出、アーキテクチャ、tty 判定）
- [ ] `CommandResolver`（PATHEXT / 実行ビット判定）
- [ ] `CommandRunner`（キャプチャ経路 + stdio 継承経路）
- [ ] `BuildContext` / `StagingProgress`（flock の P/Invoke 含む）
- [ ] `Doctor` / `DebugReporter` / `ResourceMonitor` / `Initializer`

### Phase 3 — CLI 表層

- [ ] System.CommandLine で全コマンドを定義（`version` `init` `doctor` `config` `build` `up` `sync` `stop` `down` `exec` `run` `shell` `logs` `dispatch`）
- [ ] グローバルオプション（`--config` `--env-file` `--debug` `--debug-log`）
- [ ] 未知コマンド → `wip.yml` の `commands:` へのフォールバック
- [ ] **help 出力の文言を Ruby 版と突き合わせる**（Wiki のドキュメントが help を前提にしている）
- [ ] 終了コードのパリティ確認（`exit 1` / `exit 127` / `exit 130` / `128+signal`）

### Phase 4 — 実機検証

golden テストで拾えない領域を手動で潰す。以下のマトリクスを実施:

| シナリオ | Windows (win-x64) | WSL2 (linux-x64) |
|---|---|---|
| `wip init` → `doctor` → `build` → `up -d` → `exec` | ☐ | ☐ |
| `wip shell`（対話。Ctrl-C、Ctrl-D、リサイズ） | ☐ | ☐ |
| `wip run rails console`（対話 TTY） | ☐ | ☐ |
| `mode: compose-native` で `up` / `logs` | ☐ | ☐ |
| `sync` / `sync --watch` | ☐ | ☐ |
| `up --watch`（restart ポーリング） | ☐ | ☐ |
| `shadow_context` 経由のビルド | — | ☐ |
| `.dockerignore` を効かせた大きめのコンテキスト | ☐ | ☐ |
| `--debug` / `--debug-log` | ☐ | ☐ |

- [ ] 自分たちのプロジェクトで 1〜2 週間ドッグフーディング

### Phase 5 — 配布切り替え

- [ ] 最初の C# 版リリース（v2.0.0 を提案。実装言語の変更と最小要件の変更は breaking にあたる）
- [ ] winget-pkgs へ**初回 PR**（レビュー待ちの余裕を見込む）
- [ ] README / Wiki のインストール手順を更新（WinGet を主導線に、gem を移行案内に）
- [ ] gem の最終版を deprecation メッセージ付きで公開
- [ ] `lib/` `exe/` `spec/` `Gemfile` `Rakefile` `*.gemspec` `.rubocop.yml` を削除、`gem-push.yml` を撤去
- [ ] 以降のリリースは WinGet 自動 PR に載せる

---

## 9. リスクと未確定事項

### リスク

| リスク | 影響 | 緩和策 |
|---|---|---|
| **AOT でどこかのライブラリが動かない** | 高 | Phase 0 のスパイクで全依存を入れた状態を先に実測。動かなければ依存を差し替える（YAML は自前パーサ、CLI は自前パーサに退避可能） |
| **対話コマンドのエラーヒント喪失** | 中 | §5.3 選択肢 A の既知の代償。実害が出たら ConPTY/forkpty で回収 |
| **winget-pkgs 初回レビューが長引く** | 中 | リリース自体は先に打てる（ZIP は Releases にある）。WinGet はレビュー完了後に告知 |
| **Windows ネイティブ実行への移行で既存 `wip.yml` が壊れる** | 高 | 決定 1 で A（Linux も出す）を選べば回避。B を選ぶ場合は移行ガイドと `wip doctor` での検出が必須 |
| **仕様ドリフト（2 実装の乖離）** | 中 | golden テスト（§7）。CI で Ruby / C# 双方に対して実行 |
| **`BuildContext` の manifest 精度差でシャドウが毎回フル再構築される** | 低 | manifest にバージョンを持たせ、初回のみ再構築で収束させる。Phase 4 で実測 |

### 着手前に確認が必要な事項

以下は本計画時点で断定を避けた項目。**Phase 0 で必ず一次情報を確認する。**

1. **.NET のバージョン** — net10.0（LTS）を前提にしているが、着手時点のサポート状況を確認する
2. **System.CommandLine の最新版と AOT 対応の実態** — 特に「未知のコマンド名をフォールバックさせる」用途が API で素直に書けるか
3. **YamlDotNet の AOT 発行実績** — 表現モデル方式で本当にリフレクション警告ゼロか
4. **`InvariantGlobalization=true` の妥当性** — 日本語を含む `wip.yml` / パス / コンテナ出力の扱いに影響しないか
5. **GitHub Actions の arm64 ランナー** — Windows/Linux 双方の arm64 ランナーの利用可否と料金
6. **WinGet マニフェストスキーマの最新版** — `NestedInstallerType: portable` の現行の書式
7. **PackageIdentifier `Slidict.Wip`** — 発行者名の要件を満たすか

---

## 10. まとめ

- **AOT 適性は高い。** 依存が Thor のみ、YAML/JSON を辞書として扱う設計、外部 API なし — 移植の障害は少ない。
- **本当の難所は 2 つだけ**: `CommandRunner` の PTY と `BuildContext` のファイル操作。それ以外は機械的な移植。
- **最大の設計判断は「Windows ネイティブに寄せるか、WSL 内実行も維持するか」**（決定 1）。ここだけは実装前に決めきる必要がある。
- **進め方の要点**: 先に配布経路（Phase 0）を通し、次に golden テスト（§7）で安全網を張ってから中身を書く。逆順にすると、実装が終わってから配布で詰まる／気づかないうちに挙動が変わる、のどちらかを踏む。

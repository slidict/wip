# wip C# / Native AOT 移行計画

目標パイプライン:

```
C# → Native AOT → wip.exe → ZIP → GitHub Releases → WinGet
```

本ドキュメントは移行の「計画」であり、実装は含まない。合意した時点でフェーズ 0 から着手する。

---

## 1. 前提（確定事項）

| # | 決定 | 内容 |
|---|---|---|
| 1 | **ターゲット** | **Windows ネイティブ (win-x64) のみ**。Linux バイナリは出さない。WSL2 のシェルからは interop 経由で `wip.exe` を叩く |
| 2 | **Ruby 実装** | 別リポジトリにコピーして退避。本リポジトリからは削除する |
| 3 | **互換性** | **破壊的変更を許容**。既存の `wip.yml` との後方互換は要件としない |
| 4 | **リポジトリ** | C# 実装が `slidict/wip` を引き継ぐ（Wiki・Issues・Releases の履歴を維持） |

この 4 点により、当初計画で最大の論点だった「WSL 内実行を維持するか」は解消し、**単一 RID・単一 OS の素直な移植**になる。

### 実行モデル

```
┌─ Windows ─────────────────────────────────────────┐
│  winget install Slidict.Wip                       │
│         ↓                                         │
│      wip.exe ──呼び出し──> wslc.exe                │
│         ↑                                         │
│         │ WSL interop (Windows PATH が Linux PATH  │
│         │ に入るので bash から直接叩ける)           │
│  ┌──────┴──── WSL2 (Ubuntu 等) ─────────────────┐  │
│  │  $ cd ~/myproject && wip.exe up -d          │  │
│  └─────────────────────────────────────────────┘  │
└───────────────────────────────────────────────────┘
```

**wip.exe のプロセスは常に Windows 側で動く。** WSL2 は「コマンドを打つ場所」であって「実行される場所」ではない。この一点が §3 の論点を生む。

### 現状の棚卸し

| 項目 | 現状 |
|---|---|
| 実装 | Ruby 3.2+ / Thor 1.3 |
| 本体規模 | `lib/` + `exe/` で 23 ファイル・約 3,000 行 |
| テスト | RSpec 17 ファイル・約 3,400 行 |
| 配布 | RubyGems (`wslc-wip`) + GitHub Packages |
| 依存 | Thor のみ |

依存が Thor だけで、YAML/JSON を型にマッピングせず素の Hash として扱っている（`Config#stringify` 以降ずっと `@raw['dependencies']` 形式の辞書アクセス）。AOT 最大の地雷である「リフレクション依存シリアライザ」を最初から踏んでいないため、AOT 適性は高い。

---

## 2. Windows 専用化で消える作業

当初の 4 RID 案から、以下がまるごと不要になる。**移植の総量はおよそ 2〜3 割減る。**

| 消えるもの | 理由 |
|---|---|
| **openpty / forkpty の P/Invoke** | Linux 側の `CommandRunner#run_attached` が不要。§4.2 参照 |
| **flock(2) の P/Invoke** | Windows は `FileStream.Lock` が効く。`BuildContext` のロックは BCL のみで書ける |
| **`UnixFileMode` によるパーミッション保持** | 実行ビットの持ち回りが不要（`copy_entry(preserve)` の意図が消える） |
| **`/proc/version` パース** | `Environment#wsl2?` は `wsl.exe --status` 一本に |
| **`windows_interop?` チェック** | Windows 側で動く以上、常に自明。`Doctor` から削除 |
| **`/mnt/c/...` パスの特別扱い** | `CommandResolver::CANDIDATES` から `/mnt/c/Windows/System32/wslc.exe` を削除 |
| **linux-x64 / arm64 の RID** | CI マトリクスが 1〜2 ジョブに縮小 |
| **tar.gz 配布と install スクリプト** | ZIP + WinGet のみ |
| **`Gem.win_platform?` 分岐 (計 5 箇所)** | 分岐そのものが消える |

---

## 3. 🔴 中核リスク: パスモデル

**この計画で唯一、実装方針が未確定のまま残る領域。** 他はすべて機械的な移植で片付く。

### 3.1 問題

wip は**ホストの絶対パスを wslc に渡す**箇所を 3 つ持つ:

| 箇所 | 生成物 | コード |
|---|---|---|
| `sync:` のソースマウント | `-v <ホスト絶対パス>:/host-src:ro` | `SyncSettings#volume_specs` |
| `volumes:` のバインドマウント | `-v <ホスト絶対パス>:/app` | `CommandBuilder#volume_specs` |
| ビルドコンテキスト | `wslc build` の cwd | `CLI#run_staged_build` |

WSL2 のシェルから Windows exe を起動すると、その Windows プロセスの作業ディレクトリは **UNC パス** になる（`\\wsl.localhost\<distro>\home\user\proj` 形式。旧表記は `\\wsl$\...`）。

したがって `~/myproject` で `wip.exe up` を叩くと:

```
sync.source  = \\wsl.localhost\Ubuntu\home\user\myproject
生成される -v = \\wsl.localhost\Ubuntu\home\user\myproject:/host-src:ro
```

**この `-v` を wslc が受け付けるかは未知数**であり、受け付けない可能性が高い。現行 Ruby 実装は WSL 内で動いていたので `source` は `/home/user/myproject` という素直な Linux パスになっており、この問題自体が存在しなかった。

なお現行コードには既に `wslc build` が絶対パスのコンテキストでクラッシュする（`ERROR_UNHANDLED_EXCEPTION`）という回避策コメントがあり、chdir + `"."` で凌いでいる。wslc のパス処理は元々素直ではない。

### 3.2 対応方針（決定木）

**Phase 0 の最優先スパイクで、wslc.exe が実際に何を受け付けるかを実測する。** 結果に応じて 3 分岐:

| 実測結果 | 採る方針 |
|---|---|
| **(a) wslc が Linux パスを受け付ける** | wip.exe が UNC → Linux パスに変換する（`\\wsl.localhost\Ubuntu\home\u\p` → `/home/u/p`）。**最もきれい。** wip.exe 内で完結し、外部プロセス起動も不要 |
| **(b) wslc が Windows ローカルパスのみ受け付ける** | ビルドコンテキストは §3.3 の常時ステージングで解決。だが `volumes:` / `sync.source` の WSL 側バインドマウントは**成立しない** → プロジェクトを Windows 側 FS (`C:\...`) に置くことが要件になる。**要ドキュメント化された制約** |
| **(c) wslc が UNC をそのまま扱える** | 変換不要。ただし 9p 越しの I/O 性能が実用に耐えるか別途計測が要る |

**現時点の見立ては (a) または (b)。** wslc はコンテナを WSL2 の VM 内で動かす以上、Linux パス表現を持っているはずで (a) の目が高いが、**確認せずに実装方針を決めない。**

### 3.3 ビルドコンテキストは常時ローカルステージングにする

パス問題のうち**ビルドコンテキストだけは方針が確定できる**。

現行の `shadow_context` は「WSL 側ソースを Windows 側にミラーして速くする」オプトイン機能だった。新モデルでは、UNC 越しのソースを wslc に直接読ませる構図になるため、**同じ機構がオプションではなく常に必要**になる。

したがって:

- `BuildContext` は**常に** Windows ローカル（`%LOCALAPPDATA%\wip\contexts\<sha256>`）にステージングする
- `shadow_context` 設定キーは**廃止**し、キャッシュ位置を変えたい場合の任意設定 `context_cache` に置き換える（あるいはキーごと廃止）
- 既存の増分 manifest 機構（変更ファイルのみコピー）はそのまま活かす
- `wslc build` には常にローカルパスの cwd + `"."` を渡す → §3.1 のクラッシュ回避策も自然に満たす

**性能上の注意:** UNC 越しのファイル走査は 9p プロトコル経由で遅い。manifest の fingerprint 取得を「1 ファイルずつ `stat`」で実装すると大きなツリーで致命的に遅くなる。`Directory.EnumerateFileSystemEntries` は列挙時に `FindFirstFile` のデータ（サイズ・mtime・属性）を同時に取れるので、**列挙結果から属性を取る実装にする**。ここは Phase 4 で実測する。

### 3.4 mtime 精度の変更

Ruby は `stat.mtime.nsec`（ナノ秒）、.NET は `File.GetLastWriteTimeUtc().Ticks`（100ns）。manifest のフォーマットが変わるため、**manifest にスキーマバージョンを持たせ、不一致なら 1 回だけフル再構築**して収束させる。

### 3.5 `wip` と `wip.exe` の呼び分け（UX）

WinGet の portable インストールは `wip.exe` という shim を links ディレクトリに置き、PATH を通す。

- **PowerShell / cmd から**: `wip` で動く（PATHEXT が効く）
- **WSL2 の bash から**: **`wip.exe` と拡張子まで打つ必要がある**。bash は PATHEXT を知らない

拡張子なしで叩きたい場合、ユーザー側に `alias wip=wip.exe` などの一手間が要る。これを吸収する案:

- `wip.exe install-wsl-shim` サブコマンドを用意し、WSL 内の `/usr/local/bin/wip` に `exec "$(which wip.exe)" "$@"` の 2 行スクリプトを書き込む
- あるいは README に alias の追記手順を書くだけに留める

**Phase 3 で判断する。** 機能としては小さいが、WSL2 が主たる利用場所である以上、体験差は大きい。

---

## 4. 残る技術課題

パスモデル以外に、判断が要るのは以下の 2 点のみ。

### 4.1 YAML パース（低リスク）

現行実装が YAML をプレーンな Hash として扱っているため、**表現モデル（ノードツリー）でパースすればリフレクションは一切発生しない**:

- `YamlDotNet` の `YamlStream` / `YamlMappingNode` を使う（デシリアライザを通さない）
- Ruby 実装のコード形状をほぼそのまま写せるので、移植時のバグ混入も減る

`ConfigLoader` は既に `YAML.safe_load_file(permitted_classes: [], aliases: false)` でアンカー/エイリアスを禁止済み。**この制約は C# 側でも維持する**（実装が単純になる）。

JSON（`wslc list --format json` の読み取り）は `System.Text.Json` の `JsonDocument` がリフレクション不使用でそのまま AOT 動作する。POCO へのデシリアライズはしない。

### 4.2 対話コマンドの端末制御

現行の `CommandRunner` は 3 経路（パイプ / Linux openpty / Windows stdio 継承）を持つが、**Windows 専用化で openpty 経路が消え、2 経路に減る**。

残る判断は、対話コマンド（`wip shell`、`wip exec -it`、`wip run rails console`）で:

| 選択肢 | 内容 | 評価 |
|---|---|---|
| **A（推奨）** | **stdio 継承**（`RedirectStandard* = false`）。現行 Windows 経路と同じ | ジョブ制御・Ctrl-C・isatty 判定は正しく動く。**代償: 対話コマンドで `ErrorInterpreter` のヒントが出せない**（Windows では現状も出ていないので機能後退ではない） |
| B（将来） | ConPTY (`CreatePseudoConsole`) を P/Invoke | 出力キャプチャと対話性を両立できるが、実装量が大きい |

**推奨: A。** 非対話経路（`probe` / `resource_exists?` / 通常の `execute`）はキャプチャを維持するので、`wip up` / `wip doctor` のヒントは従来どおり出る。

**要スパイク:** WSL2 の bash から起動された Windows プロセスに、対話に耐える実コンソールが割り当たるか。Windows Terminal の ConPTY 経由になるはずだが、`wslc exec -it` が正しく TTY を認識するかは実測が要る（Phase 0）。

### 4.3 その他の細部

| Ruby | C# | 備考 |
|---|---|---|
| `Shellwords.split` | **自前実装（50 行程度）** | BCL に相当機能なし。`command:` 文字列 → argv の分割に計 4 箇所で必要。golden テストで突き合わせる |
| `File.rename`（アトミック置換） | `File.Move(src, dst, overwrite: true)` | |
| `Find.prune` によるツリー枝刈り | `EnumerateFileSystemEntries` の手動再帰 | `RecurseSubdirectories` では枝刈りできない |
| `Data.define` | `record` | |
| `Open3.popen3` | `Process` + `ArgumentList` | **引数配列渡し（シェル解釈なし）という wip の設計上重要な性質はそのまま保てる** |
| `Signal.trap('WINCH')` | 不要 | PTY 経路が消えるため |
| 正規表現（`ErrorInterpreter` 等） | `[GeneratedRegex]` | ソース生成。AOT 相性◎ |

---

## 5. 決定が必要な残件

### 決定 A: CLI フレームワーク

| 候補 | AOT 適性 | 備考 |
|---|---|---|
| **System.CommandLine 2.0（推奨）** | ◎ 公式に AOT 対応を掲げている | Thor 相当（サブコマンド、グローバルオプション、help 生成）が揃う |
| Spectre.Console.Cli | △ | コマンド型の解決にリフレクションを使う箇所があり AOT で追加対応が要る |
| 自前パーサ | ◎ | 依存ゼロ・最小サイズだが help 生成とエラー文言を全部書くことになる |

**推奨: System.CommandLine 2.0。** 着手時に最新版と AOT 対応状況を確認する（§9）。

`cli.rb` にある Thor 回避用の独自ロジック 2 つは、そのまま移植せず再設計する:

1. `reorder_global_options`（`wip --config foo up` の並べ替え）→ System.CommandLine のグローバルオプションは元々位置非依存なので、**処理ごと不要になる可能性が高い**
2. `dispatch` フォールバック（未知のコマンド名を `wip.yml` の `commands:` として解決）→ 未マッチ時ハンドラを自前で挿す必要あり。**要スパイク**

### 決定 B: arm64 を出すか

Windows on ARM でも WSL2 は動く。WinGet マニフェストは x64 / arm64 を併記できる。

**推奨: 初回は win-x64 のみ。** 命名規則（§7.3）に arm64 を後から足せる形を用意しておき、要望が出たらジョブを 1 つ増やす。arm64 Windows ランナーの可用性確認が要る。

### 決定 C: コード署名

未署名の exe は SmartScreen の警告対象になりうる。ZIP + portable は MSI より警告に当たりにくいが、レピュテーションはゼロから積むことになる。

**推奨: 当面は署名なしで出し、実害が出たら Azure Trusted Signing を検討。** 後から CI に署名ステップを 1 つ足すだけなので、初期の障害にはしない。

### 決定 D: バージョン番号

実装言語の変更・最小要件の変更・`wip.yml` の破壊的変更が同時に起きるため、**v2.0.0 から開始**することを推奨する。

あわせて、退避先の Ruby リポジトリでは gemspec の `source_code_uri` / `bug_tracker_uri` を新リポジトリに向け直すこと（現在は `slidict/wip` を指している）。

---

## 6. リポジトリ構成

```
wip/
├── Directory.Build.props        # net10.0 / LangVersion / AOT 設定 / Version
├── Directory.Packages.props     # Central Package Management
├── wip.slnx
├── src/
│   ├── Wip.Core/                # ロジック
│   │   ├── Configuration/       # Config, ConfigLoader, DotenvLoader, SyncSettings
│   │   ├── Compose/             # ComposeFile, ComposeBridge, VariableInterpolation
│   │   ├── Build/               # BuildContext, DockerIgnore, StagingProgress
│   │   ├── Execution/           # CommandBuilder, CommandRunner, CommandResolver, CommandDisplay
│   │   ├── Diagnostics/         # Doctor, ErrorInterpreter, DebugReporter, ResourceMonitor
│   │   └── Platform/            # WindowsEnvironment, WslPath, Shellwords
│   └── Wip.Cli/                 # PublishAot=true。エントリポイントとコマンド定義のみ
├── tests/
│   ├── Wip.Tests/               # xUnit（通常の CoreCLR で実行）
│   └── golden/                  # 移行パリティ用フィクスチャ（§8）
└── packaging/winget/            # WinGet マニフェストのテンプレート
```

`Wip.Core` と `Wip.Cli` を分けるのはテスト容易性のため。AOT 発行は `Wip.Cli` にのみ適用し、`Wip.Core` は xUnit から通常参照する。ただし `Wip.Core` にも `<IsAotCompatible>true</IsAotCompatible>` を立て、リフレクション依存をコンパイル時に検出させる。

**`Platform/WslPath` が新規追加分**（§3 の UNC ↔ Linux パス変換）。

### 共通ビルド設定（`Directory.Build.props` の要点）

```xml
<TargetFramework>net10.0</TargetFramework>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<Nullable>enable</Nullable>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<InvariantGlobalization>true</InvariantGlobalization>   <!-- ICU を落としてサイズ削減 -->
<UseSystemResourceKeys>true</UseSystemResourceKeys>
<PublishAot>true</PublishAot>                            <!-- Wip.Cli のみ -->
<StripSymbols>true</StripSymbols>
<IlcOptimizationPreference>Size</IlcOptimizationPreference>
```

`InvariantGlobalization=true` の妥当性は要確認（§9）。`SECRET_PATTERN` のような大文字小文字無視比較は `StringComparison.OrdinalIgnoreCase` を明示すれば問題ない。

### サイズと起動時間の目標

| 指標 | 目標 |
|---|---|
| `wip.exe` サイズ | 圧縮前 < 10 MB / ZIP 後 < 5 MB |
| `wip version` 実行時間 | < 30 ms（現行 Ruby は 200–400 ms） |
| ZIP 内容 | `wip.exe` 単体 |

---

## 7. モジュール別 移植計画

| Ruby | 行数 | C# 移植先 | 難度 | 備考 |
|---|---:|---|:---:|---|
| `version.rb` | 5 | `Directory.Build.props` の `<Version>` | 易 | 単一の版元をここに移す |
| `errors.rb` | 7 | `WipException` 階層 | 易 | |
| `command_display.rb` | 19 | `CommandDisplay` | 易 | |
| `dotenv_loader.rb` | 39 | `DotenvLoader` | 易 | 正規表現をそのまま移植 |
| `environment.rb` | 43 | `Platform/WindowsEnvironment` | **易**（↓） | `/proc/version` と interop 判定が消えて `wsl.exe --status` のみに |
| `command_resolver.rb` | 48 | `CommandResolver` | **易**（↓） | PATHEXT のみ。Unix 実行ビット判定が不要に |
| `variable_interpolation.rb` | 60 | `VariableInterpolation` | 易 | |
| `staging_progress.rb` | 63 | `StagingProgress` | 易 | |
| `debug_reporter.rb` | 66 | `DebugReporter` | 易 | |
| `compose_bridge.rb` | 72 | `ComposeBridge` | 易 | |
| `docker_ignore.rb` | 75 | `DockerIgnore` | 中 | glob マッチを自前実装。振る舞い一致が重要 |
| `error_interpreter.rb` | 90 | `ErrorInterpreter` | 易 | `[GeneratedRegex]` 化 |
| `resource_monitor.rb` | 94 | `ResourceMonitor` | 中 | |
| `sync_settings.rb` | 157 | `SyncSettings` | **中→難**（↑） | `source` のパス表現が §3 の結論に依存 |
| `doctor.rb` | 158 | `Doctor` | **易**（↓） | interop チェック削除。WSL2 検出が単純化 |
| `initializer.rb` | 220 | `Initializer` | 中 | テンプレートは raw string literal (`"""`) |
| `build_context.rb` | 219 | `BuildContext` | **中**（↓） | flock/UnixFileMode の P/Invoke が不要に。代わりに常時ローカルステージング化（§3.3） |
| `command_builder.rb` | 234 | `CommandBuilder` | 中 | `Shellwords.split` が要る。`volume_specs` は §3 の結論に依存 |
| `config.rb` | 273 | `Config` | 中 | `shadow_context` 検証を削除 or `context_cache` に置換 |
| `compose_file.rb` | 272 | `ComposeFile` | 中 | 同上 |
| `command_runner.rb` | 205 | `CommandRunner` | **中**（↓） | 3 経路 → 2 経路。openpty/raw モード/SIGWINCH がまるごと消える |
| `cli.rb` | 541 | `Wip.Cli` | 中 | System.CommandLine への読み替え |
| — | — | `Platform/WslPath` | **難**（新規） | §3。UNC ↔ Linux パス変換 |
| — | — | `Platform/Shellwords` | 中（新規） | POSIX shell 準拠の分割 |

（↑↓ は当初の 4 RID 案からの難度変化）

---

## 8. パリティ担保（golden テスト）

**Ruby 実装がこのリポジトリを離れる前に、フィクスチャを抽出しておく。** これが移行全体の安全網になる。

`tests/golden/` に入出力のペアを置く:

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
- `Config#to_h` の正規化結果 / `DotenvLoader` / `DockerIgnore#ignored?` / `Shellwords.split` / `ComposeFile#to_dependencies_hash`

**破壊的変更を許容する以上、一部のフィクスチャは意図的に変わる。** 具体的には §3 のパス関連（`sync.source`、`volumes:`、`shadow_context`）。したがって golden テストの役割は「完全一致の強制」ではなく、**「意図しない変更の検出」**である:

- パス関連のケースは `expect` を新モデルに合わせて**意図的に書き換える**（差分がレビューに乗る）
- それ以外の 8 割は**そのまま緑であるべき**

`CommandRunner` の端末挙動と `BuildContext` の実ファイル操作は golden 化できないため、§9 Phase 4 の手動テストマトリクスで担保する。

---

## 9. 配布パイプライン

### 9.1 全体像

```
git tag v2.0.0
      │
      ▼
┌─ release.yml (windows-latest) ───────────────┐
│  dotnet publish -r win-x64                   │
│  → wip.exe → ZIP → SHA256                    │
│  → actions/attest-build-provenance           │
└──────────────────┬───────────────────────────┘
                   ▼
        GitHub Release（release-drafter の下書きを publish）
                   │  on: release published
                   ▼
        winget.yml → microsoft/winget-pkgs へ PR 自動作成
```

Native AOT はクロス OS ビルドができないため **windows ランナー必須**。ただし Windows 専用化により**マトリクスは 1 ジョブで済む**（arm64 を出す場合のみ 2 ジョブ）。

### 9.2 リリーストリガの変更

現行は `Changelog` ワークフロー成功 → `workflow_run` → `gem-push` という連鎖で追いにくい。**タグ駆動に単純化する**:

- `git tag v2.0.0 && git push --tags` → `release.yml` 起動
- バージョンの単一の版元は `Directory.Build.props` の `<Version>`。CI がタグとの一致を検証し、不一致なら fail
- `bump-version.yml` は `version.rb` ではなく `Directory.Build.props` を書き換えるよう改修
- release-drafter によるリリースノート生成はそのまま活かす
- `gem-push.yml` は削除

### 9.3 成果物の命名

```
wip-2.0.0-win-x64.zip
SHA256SUMS
```

WinGet マニフェストは URL にバージョンを埋め込むため、**この命名規則は一度決めたら変えない**（arm64 を後から足す前提の形にしてある）。

### 9.4 WinGet マニフェスト

`InstallerType: zip` + `NestedInstallerType: portable` を使う。ZIP に exe を 1 つ入れるだけの構成に対応した仕組みで、今回の形にそのまま合致する（マニフェストスキーマ 1.6 以降）。

```yaml
# Slidict.Wip.installer.yaml（骨子）
PackageIdentifier: Slidict.Wip
PackageVersion: 2.0.0
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
  - RelativeFilePath: wip.exe
    PortableCommandAlias: wip
Installers:
  - Architecture: x64
    InstallerUrl: https://github.com/slidict/wip/releases/download/v2.0.0/wip-2.0.0-win-x64.zip
    InstallerSha256: <sha256>
ManifestType: installer
ManifestVersion: 1.6.0
```

必要なファイルは 3 点セット（version / installer / locale）。

**準備が要るもの:**

1. **PackageIdentifier の確定** — `Slidict.Wip` を想定。publisher 部分が実在の発行者名と対応している必要がある
2. **microsoft/winget-pkgs のフォーク** — 自動 PR の宛先
3. **PAT (classic, `public_repo` スコープ)** — リポジトリシークレットに登録。`GITHUB_TOKEN` では他リポジトリに PR を出せない
4. **自動化アクション** — `vedantmgoyal9/winget-releaser`（zip/portable 対応）または `wingetcreate update` を CI から呼ぶ

**注意点:**

- 初回投稿は winget-pkgs 側の人手レビューが入り、マージまで数日かかることがある。**Phase 5 に余裕を持たせる**
- 検証には published な（draft/prerelease でない）リリースが必要 → WinGet ジョブは `on: release: types: [published]`
- portable パッケージは winget が links ディレクトリに shim を作り PATH を通す。**WSL の bash からは `wip.exe` と打つ必要がある**（§3.5）

---

## 10. フェーズ分割

### Phase 0 — スパイクとパイプライン検証

**方針: ロジックを 1 行も書く前に、(1) パスモデルの結論を出し、(2) 配布経路を最後まで通す。** 実装より先にこの 2 つを潰す。

**🔴 スパイク 1: パスモデル（最優先・他の判断がここに依存する）**

- [ ] WSL2 bash から Windows exe を起動したときの作業ディレクトリを実測（UNC になるか、どの表記か）
- [ ] `wslc.exe run -v` に (a) Linux パス (b) Windows ローカルパス (c) UNC パス をそれぞれ渡し、**何が通るか実測**
- [ ] `wslc.exe build` に UNC の cwd を渡した場合の挙動
- [ ] → §3.2 の決定木で方針確定。**(b) だった場合は「プロジェクトは Windows FS に置く」制約を確定させ、README/Wiki に明記する**

**🔴 スパイク 2: 対話端末**

- [ ] WSL2 bash から起動した Windows プロセスで `wslc exec -it` が TTY を正しく認識するか
- [ ] Ctrl-C・ウィンドウリサイズが期待どおり効くか

**スパイク 3: AOT 実現性**

- [ ] .NET 10 SDK でソリューション骨格を作成
- [ ] YamlDotNet（表現モデル方式）+ System.CommandLine を入れた状態で AOT 発行が通るか、リフレクション警告ゼロか
- [ ] `wip version` だけ返すダミーを発行し、**サイズと起動時間を実測**
- [ ] System.CommandLine で「未知コマンド → `commands:` フォールバック」が書けるか（§5 決定 A-2）

**パイプライン検証**

- [ ] windows ランナーでビルド → ZIP → プレリリース公開までを CI で通す
- [ ] WinGet マニフェストを手動生成し、`winget validate` / `winget install --manifest` でローカル検証（**PR は出さない**）

**完了条件: ダミー wip.exe が WinGet ローカルマニフェスト経由でインストールでき、WSL2 の bash から `wip.exe version` が動く。**

### Phase 1 — Ruby 退避と安全網

- [ ] `tests/golden/` のフィクスチャを既存 RSpec から抽出、**Ruby 側で緑を確認**
- [ ] Ruby 実装を別リポジトリへコピー（gemspec のメタデータ URL を新リポジトリに向け直す）
- [ ] 本リポジトリから `lib/` `exe/` `spec/` `Gemfile` `Rakefile` `*.gemspec` `.rubocop.yml` を削除
- [ ] `gem-push.yml` を削除、`test.yml` を dotnet 用に差し替え

### Phase 2 — 純粋ロジック層

- [ ] `Shellwords` / `DotenvLoader` / `DockerIgnore` / `VariableInterpolation` / `ErrorInterpreter`
- [ ] `WslPath`（Phase 0 スパイク 1 の結論を実装）
- [ ] `Config` / `ConfigLoader` / `SyncSettings` / `ComposeFile` / `ComposeBridge`
- [ ] `CommandBuilder`
- [ ] **完了条件: golden テストが全緑（パス関連の意図的な差分を除く）**

### Phase 3 — 実行・IO 層と CLI

- [ ] `WindowsEnvironment` / `CommandResolver`
- [ ] `CommandRunner`（キャプチャ経路 + stdio 継承経路）
- [ ] `BuildContext`（常時ローカルステージング）/ `StagingProgress`
- [ ] `Doctor` / `DebugReporter` / `ResourceMonitor` / `Initializer`
- [ ] System.CommandLine で全コマンド定義（`version` `init` `doctor` `config` `build` `up` `sync` `stop` `down` `exec` `run` `shell` `logs` `dispatch`）
- [ ] グローバルオプション（`--config` `--env-file` `--debug` `--debug-log`）
- [ ] 未知コマンド → `wip.yml` の `commands:` へのフォールバック
- [ ] **help 出力の文言を Wiki の記述と突き合わせる**
- [ ] 終了コードのパリティ（`1` / `127` / `130`）
- [ ] `install-wsl-shim` の要否を判断（§3.5）

### Phase 4 — 実機検証

golden テストで拾えない領域を手動で潰す。**PowerShell と WSL2 bash の両方から**実施:

| シナリオ | PowerShell | WSL2 bash |
|---|---|---|
| `wip init` → `doctor` → `build` → `up -d` → `exec` | ☐ | ☐ |
| `wip shell`（対話。Ctrl-C、Ctrl-D、リサイズ） | ☐ | ☐ |
| `wip run rails console`（対話 TTY） | ☐ | ☐ |
| `mode: compose-native` で `up` / `logs` | ☐ | ☐ |
| `sync` / `sync --watch` | ☐ | ☐ |
| `up --watch`（restart ポーリング） | ☐ | ☐ |
| **プロジェクトが WSL FS 上（`~/proj`）** | ☐ | ☐ |
| **プロジェクトが Windows FS 上（`C:\proj`）** | ☐ | ☐ |
| `.dockerignore` を効かせた大きめのコンテキスト（**UNC 越しの走査性能を計測**） | ☐ | ☐ |
| `--debug` / `--debug-log` | ☐ | ☐ |

- [ ] 自分たちのプロジェクトで 1〜2 週間ドッグフーディング

### Phase 5 — 配布

- [ ] v2.0.0 リリース
- [ ] winget-pkgs へ**初回 PR**（レビュー待ちの余裕を見込む）
- [ ] README / Wiki の全面更新
  - インストール手順を WinGet に変更
  - **§3 の結論に応じたパス制約の明記**
  - `shadow_context` 廃止の記載
  - WSL2 からの呼び出し方（`wip.exe` / alias / shim）
- [ ] 退避先リポジトリで gem の最終版を deprecation 告知付きで公開

---

## 11. リスクと未確定事項

### リスク

| リスク | 影響 | 緩和策 |
|---|---|---|
| 🔴 **wslc が WSL 側パスのバインドマウントを受け付けない** | **高** | Phase 0 スパイク 1 で最優先に確定。(b) なら「プロジェクトは Windows FS に置く」制約として文書化する。**この結論次第で `sync:` の設計意図そのものを見直す必要がある** |
| **UNC 越しのファイル走査が遅く、大きなツリーで実用にならない** | 中 | 列挙時に属性を同時取得する実装（§3.3）。Phase 4 で実測し、駄目なら走査自体を wslc/wsl.exe 側に寄せる案を検討 |
| **WSL bash から起動した exe が対話に耐えるコンソールを持たない** | 中 | Phase 0 スパイク 2。駄目なら ConPTY（§4.2 選択肢 B）へ |
| **AOT でどこかのライブラリが動かない** | 中 | Phase 0 スパイク 3 で全依存を入れた状態を先に実測。駄目なら YAML は自前パーサ、CLI は自前パーサに退避可能 |
| **winget-pkgs 初回レビューが長引く** | 低 | リリース自体は先に打てる（ZIP は Releases にある）。WinGet はレビュー完了後に告知 |
| **`wip.exe` と打たせる UX の摩擦** | 低 | §3.5。shim か alias 手順で吸収 |
| **Ruby 退避後に仕様の参照先を失う** | 中 | Phase 1 で golden フィクスチャを**先に**抽出する。退避先リポジトリも参照可能に保つ |

### 着手前に一次情報の確認が必要な事項

1. **wslc.exe のパス受け入れ仕様** — §3。本リポジトリに情報がないので実機確認しかない
2. **.NET のバージョン** — net10.0（LTS）を前提にしているが、着手時のサポート状況を確認
3. **System.CommandLine の最新版と AOT 対応の実態** — 特に未知コマンドのフォールバックが素直に書けるか
4. **YamlDotNet の AOT 発行実績** — 表現モデル方式でリフレクション警告ゼロか
5. **`InvariantGlobalization=true` の妥当性** — 日本語を含む `wip.yml` / パス / コンテナ出力の扱いに影響しないか
6. **WSL2 が Windows プロセスに設定する作業ディレクトリの表記** — `\\wsl.localhost\` か `\\wsl$\` か、WSL のバージョンで変わるか
7. **WinGet マニフェストスキーマの最新版** — `NestedInstallerType: portable` の現行書式
8. **PackageIdentifier `Slidict.Wip`** — 発行者名の要件を満たすか

---

## 12. まとめ

- **Windows 専用化により、移植量はおよそ 2〜3 割減った。** openpty・flock・UnixFileMode の P/Invoke、`/proc` パース、`Gem.win_platform?` 分岐、Linux RID とその配布経路がまるごと消える。
- **破壊的変更の許容と Ruby の退避により、両実装の並行メンテという最大の負債を回避できる。** ただし退避前に golden フィクスチャを抜くこと（Phase 1）。
- **残る本質的な難所は 1 つだけ: パスモデル（§3）。** 実行体が Windows 側に移ることで、ホスト絶対パスを wslc に渡す 3 箇所（`sync.source`、`volumes:`、ビルドコンテキスト）の意味が変わる。ビルドコンテキストは常時ローカルステージングで解決できるが、**バインドマウント 2 箇所は wslc の実仕様を測るまで方針を決められない。**
- **進め方の要点**: Phase 0 でパスモデルの結論を出し、配布経路を通す。次に Ruby が去る前に golden フィクスチャを抜く。この 2 つを先にやれば、以降は機械的な移植になる。

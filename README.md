# wip

`wip` は、Docker 開発環境 CLI の dip に近い操作感を Microsoft WSLC に提供する Ruby 製 OSS CLI ラッパーです。プロジェクト固有のコンテナ、イメージ、環境変数やコマンドを `wip.yml` にまとめ、安全な引数配列として `wslc.exe` / `wslc` に渡します。

> **Status:** 初期リリース 0.1.0。WSLC 自体の仕様変更に追随が必要になる可能性があります。

## 対応環境とインストール

Ruby 3.2 以上、WSL2、および Microsoft WSLC が必要です。RubyGems 公開後は次で導入できます。

```bash
gem install wip
```

ソースからは `bundle install && bundle exec exe/wip version` を使用します。

## クイックスタート

```bash
gem install wip
cd my-project
wip doctor
wip build
wip rails console
```

## 完全な設定例

プロジェクトルートに `wip.yml` を置きます。子ディレクトリから実行しても親方向へ探索され、`--config PATH` で明示指定もできます。

```yaml
version: 1
wslc:
  command: auto # wslc.exe、wslc、System32 の順。絶対パスも指定可能
defaults:
  container: app
  image: slidict/slidict:development
  workdir: /app
  interactive: false
  remove: true
  env:
    RAILS_ENV: development
    PORT: "3000"
  ports:
    - "3000:3000"
  volumes:
    - ".:/app"
commands:
  rails:
    type: exec
    command: bin/rails
    container: app
    interactive: true
    workdir: /app
    env:
      RAILS_ENV: development
  bundle:
    command: bundle
  rspec:
    command: bundle exec rspec
  shell:
    command: bash
    interactive: true
  migrate:
    type: run
    command: bundle exec rails db:migrate
    image: slidict/slidict:development
    remove: true
  build:
    type: build
    context: .
    tag: slidict/slidict:development
```

`env` の値は文字列化されます。`config` の表示では token、password、secret、credential、auth を含むキーを伏せます。秘密値は可能なら設定ファイルではなく実行環境側で管理してください。

## コマンド

| コマンド | 説明 |
|---|---|
| `wip version` | wip と、検出できた WSLC のバージョン |
| `wip doctor` | WSL2、相互運用、WSLC、設定、アーキテクチャ、Git を診断 |
| `wip config` | デフォルト適用済み設定（秘密値をマスク） |
| `wip build -- --no-cache` | build 定義でイメージをビルド |
| `wip exec [--no-interactive] COMMAND...` | 既存コンテナ内で実行 |
| `wip run [--no-interactive] COMMAND...` | `--rm` の新規コンテナで実行 |
| `wip shell` | 定義済み shell、または `bash`、`sh` の順に起動 |
| `wip NAME ARGS...` | `commands.NAME` を実行し、引数を末尾に追加 |

TTY はコマンド設定、CLI オプション、標準入出力が双方 TTY かを合わせて判定します。`WIP_DEBUG=1` では `Shellwords.join` した実行コマンドを表示します。

## doctor

`[OK]`、`[WARN]`、`[FAIL]` で各診断を表示します。警告だけなら終了コード 0、WSL2/相互運用/WSLC/設定など実行を妨げる問題があれば 1 です。実際のビルド環境から Git が見えない場合は警告になります。

## よくあるエラー

### WSLC がない

WSLC/WSL コンテナツールをインストールまたは更新し、`wip doctor` を実行してください。`auto` は `wslc.exe`、`wslc`、`/mnt/c/Windows/System32/wslc.exe` の順で探します。

### Docker Hub の認証

`pull access denied` 等を検出するとログイン方法を補足します。

```bash
wslc registry login -u <username> docker.io
```

### CPU アーキテクチャ不一致

イメージを確認し、amd64/arm64 のマルチアーキテクチャイメージを公開します。

```bash
docker buildx imagetools inspect <image>
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t <image> \
  --push .
```

## 開発

```bash
git clone <repository-url>
cd wip
bundle install
bundle exec rspec
bundle exec rubocop
bundle exec rake
```

テストは WSLC を必要とせず、解決・構築・実行層を差し替え可能にしています。GitHub Actions は Ruby 3.2、3.3、3.4 で RSpec と RuboCop を実行します。

## 初期リリースに含まれないもの

Compose 互換、常駐プロセス、GUI、PowerShell 最適化、レジストリ API/manifest の直接解析、自動更新、プラグインは未実装です。将来は設定スキーマ、ライフサイクルフック、複数コンテナ、プラットフォーム選択、より詳細な診断の追加が有用です。

## License

[MIT License](LICENSE)

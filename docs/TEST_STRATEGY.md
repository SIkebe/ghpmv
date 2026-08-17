# テスト戦略

このドキュメントは、`ghpmv` のテストをどの層で担保し、どの変更でどの検証を行うかをまとめたものです。GEI と組み合わせた手動のエンドツーエンド検証手順は [MANUAL_TEST_PLAN.md](MANUAL_TEST_PLAN.md) にまとめています。

## 基本方針

`ghpmv` の移行処理の多くは決定的な .NET ロジックですが、重要な機能は実際の GitHub GraphQL API と GitHub Projects の Web UI に依存します。そのため、速いローカルテストだけで完結させず、次の層を組み合わせて品質を担保します。

- ネットワーク不要のロジックは単体テストで高速に検証する。
- GraphQL のスキーマ、権限、ページネーション、mutation の前提は実 API 統合テストで確認する。
- GitHub UI の変更に弱いブラウザー自動化は専用 E2E として分離する。
- 配布物は publish 後に `--version` が起動することを smoke test で確認する。
- リリース前や手順検証前は、GEI + `ghpmv` の手動 E2E で実運用に近い流れを確認する。

## テスト層

| 層 | 場所 | 目的 | 外部依存 | 実行タイミング |
|---|---|---|---|---|
| 単体・ロジックテスト | `tests/Ghpmv.Core.Tests`、`tests/Ghpmv.Browser.Tests` の非 E2E テスト | CSV 解析、snapshot model、mapping、verify 差分、import conflict、UI snapshot serialization を検証する。 | なし | ローカルの .NET 変更と、.NET に影響する PR。 |
| 実 API 統合テスト | `tests/Ghpmv.Integration.Tests` | GraphQL 接続、export/import/verify、View の API 設定、organization Issue Field lifecycle、collaborator/repository link、user-owned project、item resume/relink を実 GitHub API で検証する。 | `GHPMV_TEST_TOKEN` と fixture 用 org/project 変数。ローカルと資格情報のない PR では skip。 | secrets が使えるリポジトリ CI の .NET 変更 PR、毎日の scheduled run、手動実行、API 変更時のローカル検証。 |
| ブラウザー E2E テスト | `tests/Ghpmv.Browser.Tests` の E2E テスト | Playwright と GitHub Projects UI 経由で collaborator export、View round-trip、Workflow round-trip を検証する。 | `GHPMV_BROWSER_STATE`、`GHPMV_TEST_TOKEN`、source/target fixture org。未設定時は skip。 | `src/Ghpmv.Core/Browser` 変更時とリリース前に手動実行。scheduled/nightly は未実装。 |
| 手動移行テスト | [MANUAL_TEST_PLAN.md](MANUAL_TEST_PLAN.md) | GEI repository migration、`ghpmv export`、mapping CSV 補完、`ghpmv import`、`ghpmv verify`、UI 目視確認までの実運用フローを検証する。 | source/target org、PAT、browser profile、必要に応じて EMU/GHEC-DR 環境。 | リリース候補前、移行手順の検証前。 |
| CI packaging smoke test | `.github/workflows/ci.yml` | Release build、self-contained publish、framework-dependent publish、`--version` 起動を確認する。 | GitHub Actions runner、`global.json` で指定した .NET SDK。 | .NET に影響する PR の build/test 成功後。 |

`IssueFieldLifecycleIntegrationTests` は共有 fixture の既存 field を再利用せず、毎回一意な organization Issue Field を作成します。これにより `createIssueField` の初回作成経路を必ず通し、Project への link、read-back、update、delete まで確認します。

## ローカル実行

まず、認証情報がなくても実行できる deterministic suite を回します。実 API 統合テストと browser E2E は、それぞれ必要な環境を用意したうえで別に実行します。

```powershell
dotnet restore Ghpmv.slnx
dotnet build Ghpmv.slnx -c Release --warnaserror
dotnet test tests/Ghpmv.Core.Tests/Ghpmv.Core.Tests.csproj -c Release --no-build
dotnet test tests/Ghpmv.Browser.Tests/Ghpmv.Browser.Tests.csproj -c Release --no-build --filter "Category!=E2E"
```

変更箇所が明確な場合は、対象プロジェクトだけを回して内側のループを短くします。

```powershell
dotnet test tests/Ghpmv.Core.Tests/Ghpmv.Core.Tests.csproj -c Release
dotnet test tests/Ghpmv.Integration.Tests/Ghpmv.Integration.Tests.csproj -c Release
dotnet test tests/Ghpmv.Browser.Tests/Ghpmv.Browser.Tests.csproj -c Release --filter "Category!=E2E"
```

CLI 起動、version 表示、packaging、Playwright の配布形態に関わる変更では publish smoke test も行います。

```powershell
dotnet publish src/Ghpmv.Cli -c Release --self-contained true -r win-x64 -o artifacts/sc-win-x64
artifacts/sc-win-x64/ghpmv.exe --version

dotnet publish src/Ghpmv.Cli -c Release --self-contained false -o artifacts/fdd
dotnet artifacts/fdd/ghpmv.dll --version
```

## 環境変数が必要なテスト

実 API / browser E2E は、開発環境や fork PR で壊れないように、必要な secret や browser state が未設定なら skip します。

ここで扱う `GHPMV_TEST_*` はメンテナー向けの共有 fixture テスト用です。通常の利用者が Project を移行する際の token 権限ではありません。移行用の最小権限は README の [Token permissions](../README.md#token-permissions)、fixture を新規作成する権限は [MANUAL_TEST_PLAN.md の 4.3](MANUAL_TEST_PLAN.md#fixture-token-permissions) を参照してください。

Organization、Project番号、repository、browser profile、GEI、user mappingなどの非secret値は、コメント付きの [`tests/e2e.settings.jsonc`](../tests/e2e.settings.jsonc) にまとめています。環境固有の値は、このファイルを `tests/e2e.settings.local.jsonc` にコピーして編集してください。local fileはgitignoreされます。別の場所に置く場合は、`GHPMV_E2E_SETTINGS`にそのfile pathを設定します。

設定の優先順位は、既存の`GHPMV_TEST_*`環境変数、`tests/e2e.settings.local.jsonc`、共有`tests/e2e.settings.jsonc`の順です。PAT、browser cookie、storage-stateの内容はJSONCへ保存せず、JSONCに記載された環境変数名が参照するprocess environmentまたはfileで管理します。

実API integration suiteは既存どおり単一の`GHPMV_TEST_TOKEN`を使用し、sourceとtargetが同じGitHub deploymentにある構成を対象にします。JSONCのsource/target API originが異なる場合は誤ったhostへ書き込まず、明示的に失敗します。GitHub.comからGHEC with data residencyなどdeploymentをまたぐ検証は、source/target別tokenとbrowser stateを使うbrowser/manual E2Eで行います。

| 変数 | 使用箇所 | 意味 |
|---|---|---|
| `GHPMV_TEST_TOKEN` | 統合テスト、browser E2E | fixture org に対して SSO authorization 済みの token。 |
| `GHPMV_TEST_ORG` | 統合テスト、browser E2E | source fixture organization。未設定時はテスト側の既定値を使う。 |
| `GHPMV_TEST_TARGET_ORG` | 統合テスト、browser E2E | target fixture organization。未設定時はテスト側の既定値を使う。 |
| `GHPMV_TEST_PROJECT_NUMBER` | 統合テスト | source fixture project number。未設定時は共有 fixture 番号を使う。 |
| `GHPMV_TEST_FIXTURE_REPO` | 統合テスト | source fixture repository の short name。 |
| `GHPMV_TEST_TARGET_FIXTURE_REPO` | 統合テスト | relink テストで使う target fixture repository の short name。 |
| `GHPMV_BROWSER_STATE` | browser E2E | `ghpmv login` で作成した Playwright storage-state file の path。 |

browser state は手で編集せず、CLI で作成または更新します。

```powershell
dotnet run --project src/Ghpmv.Cli -- setup --browsers
dotnet run --project src/Ghpmv.Cli -- login --profile source
```

browser E2E が失敗した場合は、最初に次のどれかを切り分けます。

- 製品側のロジック不具合
- 認証や session の失効
- fixture の内容変更
- GitHub UI selector の変更

selector や UI 前提が変わった場合は、実装修正と合わせて [BROWSER_AUTOMATION_PLAN.md](BROWSER_AUTOMATION_PLAN.md) または `docs/ui-maps/` を更新します。

## CI 方針

PR workflow の `.github/workflows/ci.yml` は最初に PR 全体の変更 path を分類し、該当する検証だけを実行します。

1. `changes` が変更を workflow、extension、.NET 影響範囲に分類する。削除と rename の移動元も判定対象にする。
2. workflow または ghalint 設定の変更時は、`ghalint` で GitHub Actions workflow の品質を確認する。
3. extension 変更時は、`Extensions` で `.mjs` module の構文を確認する。
4. .NET に影響する変更時は、`build-test` で restore、warnings as errors の build、Ubuntu / Windows の deterministic tests を行う。
5. .NET に影響する変更時は、reusable workflow の `.github/workflows/live-api.yml` で実 GitHub API 統合テストを実行する。
6. .NET に影響する変更時は、`publish` で self-contained / framework-dependent の成果物を作成し、実行可能な成果物に対して `--version` smoke test を行う。
7. `CI gate` が、分類上必要な job がすべて成功したことを確認する。

branch ruleset が要求する既存 check 名は維持します。非該当の `ghalint`、`build-test`、`publish` は軽量な no-op として成功し、不要な SDK setup、restore、test、publish は実行しません。`Live GitHub API` は必須 check ではないため、.NET 非該当時は job 自体を skip します。

通常の `Test deterministic suites` step では、`tests/Ghpmv.Core.Tests` と `tests/Ghpmv.Browser.Tests` の非 E2E テストだけを実行します。実 API 統合テストは専用の `Live GitHub API` check に分離し、repository secrets / variables がある PR で実行します。fork PR など資格情報を利用できない PR では安全に実行を省略し、`pull_request_target` は使用しません。

`.github/workflows/live-api.yml` は毎日 18:17 UTC（03:17 JST）にも default branch から実行され、`workflow_dispatch` で手動実行できます。scheduled/manual run では token 未設定と test skip を失敗扱いにします。共有 fixture への mutation が競合しないよう、PR を含むすべての live API run は同じ concurrency group で直列化します。

## 変更内容別の検証目安

まず変更を否定できる最小のテストを回し、境界をまたぐ変更では検証範囲を広げます。

| 変更領域 | 最小検証 | 広げる条件 |
|---|---|---|
| Snapshot record、mapping CSV、verify diff logic | `tests/Ghpmv.Core.Tests` | snapshot schema や verify output が import/export contract に影響する場合。 |
| GraphQL query、pagination、rate-limit、import/export/View mutation | 関連する `tests/Ghpmv.Integration.Tests` | query/mutation contract や GitHub 権限が関わる場合。 |
| Item import の resume/relink/archive/order | `ItemImporterLogicTests` と `tests/Ghpmv.Integration.Tests/ItemImporterTests.cs` | repository / user mapping の意味が変わる場合。 |
| Browser selector、View/Workflow の UI-only settings、browser profile | `GHPMV_BROWSER_STATE` を設定した `tests/Ghpmv.Browser.Tests` | GitHub UI の挙動や UI discovery note が変わる場合。View の name/layout/filter/visible fields は実 API 統合テストでも検証する。 |
| CLI command routing、options、packaging、update check | 対象の CLI/core test と publish smoke test | release artifact や install 手順に影響する場合。 |
| ドキュメントのみ | Markdown review。コマンドを変えた場合は可能な範囲で実行確認。 | テスト setup、credential、release gate、手動移行期待値を変える場合。 |

## Fixture 運用

共有 fixture org / project はテスト対象の一部として扱います。再現性を保つため、次のルールを守ります。

- 手動 UI 編集よりも、[MANUAL_TEST_PLAN.md](MANUAL_TEST_PLAN.md) の CLI fixture setup command を優先する。
- source / target の fixture repository 名を、テストで使う環境変数と一致させる。
- 手動検証やローカル E2E の失敗で作成された target project は片付ける。
- fixture の形を意図的に変える場合は、テスト、ドキュメント、UI discovery note を同じ変更で更新する。

## リリース前チェック

リリース候補を切る前に、少なくとも次を実行または確認します。

- clean checkout で deterministic suite が成功する。
- `GHPMV_TEST_TOKEN` を設定した実 API 統合テストが成功する。
- 新しい `GHPMV_BROWSER_STATE` を使った browser E2E が成功する。
- 実行可能な supported RID について self-contained publish smoke test が成功する。
- framework-dependent publish smoke test が成功する。
- 移行挙動を変える release では、[MANUAL_TEST_PLAN.md](MANUAL_TEST_PLAN.md) の GEI + `ghpmv` 手順を通す。
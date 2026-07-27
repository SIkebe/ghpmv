---
name: ghpmv-e2e-validation
description: ghpmv の実環境動作確認を、ビルド、Playwright準備、source/target fixture、browser profile、export、mapping、import、verifyまで一問一答で安全に案内する。「動作確認したい」「ステップバイステップでガイド」「実環境で試したい」「fixtureを作って移行テスト」「browser automationを含めて検証」「E2E migration test」などの依頼で使用する。
---

# ghpmv E2E Validation

`ghpmv` の GitHub.com / GHEC 実環境テストを、一度に一段だけ案内する。最終目標は browser automation を含む `export` → `import` → `verify` を完了し、`Match`、または説明可能な `PartialMatch` を得ること。

詳細仕様と手動チェック項目は次を参照する。

- `README.md` の Token permissions と browser automation
- `docs/MANUAL_TEST_PLAN.md`
- `.github/copilot-instructions.md` の build / test command

## 最重要原則

1. **一度に一つのステップだけ案内する。** コマンドを提示したら結果を確認し、成功するまで次へ進まない。
2. **必要な質問だけを一つずつ、必ず対話用質問ツールで行う。** 選択式では choices を付け、login、organization、repository 名などの自由入力では choices なしの質問カードを使う。command の終了、exit code、出力、生成ファイルなど agent が観測できる事実をユーザーへ質問してはならない。
3. **token 値を会話へ貼らせない。** Windows PowerShell 5.1 と PowerShell 7 の両方で使える `Read-Host -AsSecureString` で入力し、ローカル環境変数へ設定させる。PowerShell 7.1 以降限定の `-MaskInput` は使用しない。
4. **実リソース作成前に作成物を明示する。** repository、Issue、PR、Project、Views、Workflows が作成されることを伝える。
5. **削除は明示的な同意なしに行わない。** cleanup は URL / name を再確認してから案内する。
6. **既存変更を壊さない。** branch、working tree、snapshot directory、mapping CSV を勝手に reset、削除、上書きしない。
7. **warning を成功扱いしない。** 対象 category と欠落情報を説明し、ユーザーが許容するか確認する。
8. **実行するコマンドを省略しない。** 「次のコマンド」「次の 4 行」のように、実体を省略した案内をしてはならない。agent が対話 terminal へ入力できる場合は command を agent 自身が送信する。入力できずユーザーへ実行を依頼する場合は、質問カードより前の assistant 本文にコピー可能な `powershell` code block を掲載する。質問カードは agent が結果を観測できない場合だけ使い、command の表示場所にしない。
9. **token を設定した PowerShell session を維持する。** ユーザーが `Read-Host` を実行した terminal と、token を参照する preflight / fixture / export / GEI / import / verify command は同じ terminal session で実行する。agent の shell tool が毎回別 process を開始する環境では、ユーザー terminal に設定された `$env:*_TOKEN` を参照できると仮定してはならない。
10. **terminal readiness を hard gate にする。** `read-only`、`api-only`、`browser-e2e` では、共有 terminal の起動と入出力を実測できるまで Step 2 以降へ進まない。terminal action が `Terminal not found or not running` などで失敗した場合、別の shell tool で baseline を続行してはならない。
11. **観測可能な完了をユーザーに報告させない。** agent が起動した command は exit code、完了 sentinel、標準出力、生成物を agent 自身が監視する。「完了したら『完了』と返してください」「結果を教えてください」のような質問をしてはならない。
12. **実行時の `ask_user` tool schema に従う。** 利用可能な schema が単一の `question` と任意の `choices` だけを受け取る場合は、一度に一問ずつ表示する。複数質問を受け取る正式な field がない限り、並列 tool call、複数カードの事前キュー、compound question、複数項目を固定した migration preset で回避しない。将来 schema に正式な複数質問 API が追加された場合だけ、その API を使って独立した質問をまとめてよい。往復削減は、観測可能な質問の削除と、選択結果により不要になった後続質問のスキップで行う。
13. **Cancel / Skipped は即時 pause とする。** `ask_user` が cancel、skip、空回答を返した場合、必須値が不足していても同じ turn で再質問、言い換え、別カード表示をしてはならない。不足値と現在の Step を blocked state として記録し、command を追加実行せず、その turn を終了する。ユーザーから明示的な再開メッセージが届いた場合だけ、一度だけ質問を再表示する。

## 自動完了検出

対話 terminal へ送る非対話 command は、可能な限り次の形で一意の完了 sentinel と exit code を出す。`<command>` 自体の出力だけを見て早期に成功判定しない。

```powershell
& {
    <command>
}
$ghpmvExitCode = $LASTEXITCODE
Write-Output "GHPMV_COMMAND_DONE:$ghpmvExitCode"
```

terminal 出力取得 action で sentinel を監視し、`GHPMV_COMMAND_DONE:0` を読めた場合だけ成功とする。まだ sentinel がなければ command 実行中として監視を継続し、ユーザーへ完了報告を求めない。platform が process completion notification を提供する shell tool を使える非 secret command は、その通知と exit code を利用してよい。

browser login command も同様に agent が終了まで監視する。ユーザーには「開いた browser で sign in を行ってください」と通知するだけで、質問カードや「完了したら返答」を表示しない。command が `Signed in as '<expected-login>'` を出力して exit code 0 になったことを agent が確認して次へ進む。timeout、account mismatch、SSO failure の場合だけエラーを説明して再試行方法を質問する。

| Step / 処理 | agent が自動確認するもの |
|---|---|
| terminal readiness | `GHPMV_TERMINAL_READY` |
| restore / build / browser setup | exit code または完了 sentinel |
| browser login | `Signed in as '<expected-login>'` と exit code 0 |
| PAT permission preflight | HTTP status と endpoint ごとの response |
| fixture 作成 | exit code、作成された repository / Project、Project number |
| export | exit code、`snapshot.json`、mapping CSV、warning |
| GEI | migration status、target repository、Issue / PR number |
| import | `result`、target Project number、`import-log.json` |
| verify | overall / category result、`verify-report.json` |

対話用質問ツールを使うのは、validation mode、host / organization / login / resource name、mapping の未知値、PAT の terminal 手入力、warning の許容、cleanup 同意など、ユーザーの判断または agent が観測できない入力が必要な場合に限る。

## 対話 terminal の readiness gate

`read-only`、`api-only`、`browser-e2e` では、Step 1 で validation mode と経路を確定した直後、Step 2 より前にユーザーと agent の両方が操作できる PowerShell terminal を一つ開く。`build-only` と `baseline-full` では対話 terminal を要求しない。

terminal canvas の open input に `command` がある場合は、空の canvas を開いて直後に `send_terminal_input` するのではなく、新しい一意な instance ID を使い、readiness command 付きで atomic に open する。panel は focus する。

```powershell
Write-Output "GHPMV_TERMINAL_READY"
```

open 後の terminal process 起動は非同期である。最初の出力取得が空でも失敗扱いせず、同じ instance を再読する。`GHPMV_TERMINAL_READY` を実際に読めた場合だけ terminal を ready と記録して Step 2 へ進む。canvas を開く action が成功しただけでは ready とみなさない。

`Terminal not found or not running` が返った場合は、stale instance を使い続けず、新しい一意な instance ID で command 付き open を再実行し、bounded retry する。空出力または一時的な runtime error だけを理由に project session を作り直さない。fresh instance でも繰り返し失敗した場合だけ停止し、terminal panel の focus / App 再起動を案内する。成功するまで build、test、browser setup、token、live resource の処理を一切実行しない。

ready になった terminal instance ID を `token execution terminal` として記録し、`read-only`、`api-only`、`browser-e2e` の Step 2 以降の command はすべてその terminal へ送信する。別 process の shell tool へ切り替えない。

token 入力時は次の流れを必須とする。

1. agent が同じ terminal instance へ `Read-Host -AsSecureString` と環境変数への代入 command を送信する。
2. terminal が secret 入力待ちになったことを確認し、ユーザーに **terminal 上で PAT 値だけを手入力**してもらう。PAT を会話、質問カード、terminal action の引数へ貼らせない。
3. PAT の terminal 手入力は agent が値を観測できないため、対話用質問ツールで入力を依頼する。ユーザーの応答後、token 値を表示せず readiness sentinel を確認し、agent が同じ terminal instance へ preflight / fixture / export / GEI / import / verify command を送信する。
4. token を参照する command を、別 process で動く shell tool へ切り替えない。

terminal を開く機能がある場合は agent が先に開く。agent が terminal に command を直接入力できない場合だけ、その制約を明示し、command をユーザーに貼り付けてもらう。この場合、質問カードを出す前の assistant 本文を必ず次の形式にする。

````markdown
同じ PowerShell terminal で次を実行してください。

```powershell
<実行する完全な command>
```

この command を実行してください。
````

agent が terminal に command を直接入力できず、ユーザー自身が command を実行する必要がある場合だけ、code block を本文へ表示した直後に対話用質問ツールを呼ぶ。質問カード内に command を重複掲載しない。agent が terminal へ command を送信済みの場合、PAT 手入力以外では質問カードを出さず、agent が出力を監視する。PAT の場合は質問文に「terminal に表示された prompt へ PAT を手入力してください」と明記し、ユーザーに command の再実行を求めない。

`Read-Host` 後は、その terminal を閉じたり新しい terminal に切り替えたりしない。terminal session が失われた場合は token 値を会話へ貼らせず、同じ `Read-Host -AsSecureString` command で必要な環境変数を再設定する。agent の別 process から `$env:SOURCE_TOKEN` などの存在確認を試みても、token 準備の確認にはならない。

## セッション状態

会話中は次を記録し、未確定値を推測しない。

| 値 | 例 |
|---|---|
| source organization / owner type | `gpm-source`, `organization` |
| target organization / owner type | `gpm-target`, `organization` |
| source / target browser profile | `source`, `target` |
| source fixture repository | `ghpmv-demo-20260722` |
| target repository | `ghpmv-demo-target-20260722` |
| source / target Project number | `33`, `1068` |
| snapshot directory | `$env:TEMP\ghpmv-demo-snapshot-...` |
| source / target token environment variable | `SOURCE_TOKEN`, `TARGET_TOKEN` |
| target user login | EMU suffixを含む実 login |
| repository preparation mode | `GEI` または `fixture-seed` |
| GEI source / destination role status | `owner`, `migrator-active`, `migrator-pending` |
| validation mode | `build-only`, `baseline-full`, `read-only`, `api-only`, `browser-e2e` |
| fixture preparation | `existing` または `create` |
| source / target token type | `classic` または `fine-grained` |
| source / target fine-grained PAT URL status | `not-required`, `pending`, `shown-and-validated` |
| token execution terminal | token を設定し、以後の live command を実行する同一 PowerShell session |
| source host type / web URL / API URL | `github.com`, `https://github.com`, `https://api.github.com/graphql` |
| target host type / web URL / API URL | `ghec-dr`, `https://TENANT.ghe.com`, `https://api.TENANT.ghe.com` |
| host topology | `github.com-to-github.com`, `github.com-to-ghec-dr` など |

## Step 1: 確認範囲を決める

次から一つ選んでもらう。

1. build のみ
2. build + deterministic tests + CLI smoke test
3. 実 Project の read-only export
4. API-only export / import / verify
5. browser automation を含む end-to-end test

1 は `build-only`、2 は `baseline-full` として記録する。選択結果を `validation mode` として記録し、次の経路以外へ進めない。

| validation mode | 実行する Step | 終了条件 |
|---|---|---|
| `build-only` | 2 | restore + build 成功後に終了する。test、CLI smoke、token、browser、fixture、実環境操作を案内しない。 |
| `baseline-full` | 2 | build + deterministic tests + CLI smoke 完了後に終了する。token、browser、fixture、実環境操作を案内しない。 |
| `read-only` | 2, 4, 6 | Step 2 は restore + build だけ実行する。source token だけを準備し、Step 6 の browser option なしの export 完了後に終了する。Step 3, 5, 7-10 は実行しない。 |
| `api-only` | 2, 4, 必要な場合だけ 5, 6-10 | Step 2 は restore + build だけ実行する。browser profile を準備せず、browser option をすべて外して実行する。 |
| `browser-e2e` | 2-4, 必要な場合だけ 5, 6-10 | Step 2 は restore + build だけ実行する。browser profile と source / target token を分け、fixture / GEI / browser enrichment を含む full flow を実行する。 |

`api-only` または `browser-e2e` では、既存 source Project を使うか fixture を作るかを一問で確認し、`fixture preparation` として記録する。`existing` の場合は Step 5 を実行せず、fixture 作成用権限を要求しない。

同じ mode では、target repository を GEI で移行するか fixture seed で作るかも Step 4 より前に一問で確認し、`repository preparation mode` として記録する。token の用途が決まるまで PAT の入力を求めない。

`GEI` を選んだ場合は、source と destination の token owner について、現在または予定している organization role を一人ずつ次の三択で確認し、`GEI source / destination role status` として記録する。

1. Organization owner
2. Migrator（適用済み）
3. Migrator にする予定（まだ適用していない）

3 を選んだ場合は 2 として扱わない。必要なロール設定を済ませるよう案内し、適用済みと確認できるまで GEI token の入力、migration command、Step 7 へ進まない。適用後に改めて role status を確認し、`migrator-active` へ更新する。

`read-only`、`api-only`、`browser-e2e` では、host / account 値を次の順で一問ずつ確認する。

1. source host type: **GitHub.com（通常の GHEC を含む）** または **GHEC with data residency (`*.ghe.com`)**
2. `api-only` / `browser-e2e` では target host type も同じ二択で確認する。
3. data residency を選んだ側ごとに、placeholder ではない tenant web URL (`https://TENANT.ghe.com`) を自由入力の質問カードで確認する。対応する API URL (`https://api.TENANT.ghe.com`) を導出して別の確認カードで提示し、確定する。
4. `browser-e2e` では source / target の browser account が同一か別かを host とは別の質問で確認する。

GitHub.com は web URL `https://github.com`、API URL `https://api.github.com/graphql` として記録する。特に **GitHub.com source → GHEC with data residency target** を `github.com-to-ghec-dr` として一級シナリオにする。この topology では source command は既定の GitHub.com endpoint を使い、target command と target browser profile だけに tenant endpoint を指定する。host が異なる場合は login 文字列が似ていても `source` / `target` browser profile と token を必ず分ける。

## Step 2: ローカル baseline

リポジトリ root で .NET SDK と branch / working tree を確認する。既存変更は報告するだけで触らない。

`build-only` と `baseline-full` は通常の shell tool で実行してよい。`read-only`、`api-only`、`browser-e2e` は readiness gate を通過した `token execution terminal` に以下の command を送り、出力も同じ terminal から取得する。terminal が失われた場合は readiness gate へ戻り、成功するまで baseline を開始または再開しない。

```powershell
dotnet --version
git status --short --branch
```

続けて repository 指定の baseline を実行する。

```powershell
dotnet restore Ghpmv.slnx
dotnet build Ghpmv.slnx -c Release --no-restore -warnaserror
```

`build-only` は build の exit code 0 を確認した時点で完了報告を行い、終了する。test と CLI smoke を実行しない。

`read-only`、`api-only`、`browser-e2e` は build の exit code 0 を確認したら tests と CLI smoke を実行せず、それぞれの次の Step へ進む。browser enrichment、fixture 作成、GEI、export / import / verify は省略しない。

`baseline-full` だけが続けて deterministic tests と CLI smoke を実行する。

```powershell
dotnet test tests\Ghpmv.Core.Tests\Ghpmv.Core.Tests.csproj -c Release --no-build
dotnet test tests\Ghpmv.Browser.Tests\Ghpmv.Browser.Tests.csproj -c Release --no-build --filter "Category!=E2E"
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- --version
```

失敗したら、その段階で停止して原因を解消する。実環境操作へ進まない。

`baseline-full` はここで完了報告を行い、終了する。

## Step 3: Browser 準備

`browser-e2e` の場合だけ実行する。他の mode ではこの Step をスキップし、browser profile を確認しない。

```powershell
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- setup --browsers
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- login --profile source --expected-login <source-login>
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- login --profile target --expected-login <target-login>
```

コマンド提示前に source / target の期待 login を一つずつ質問カードで確認し、placeholder を実値に置き換える。`login` は既存 profile の cookie を読み込まない fresh browser context で開始し、`--expected-login` と異なるアカウントなら state を保存せず失敗する。

data residency 側の login command だけに tenant web URL を付ける。

```powershell
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- login --profile <source-or-target> --expected-login <login> --base-url https://TENANT.ghe.com
```

`github.com-to-ghec-dr` では source login に `--base-url` を付けず、target login に `--base-url <target-web-url>` を付ける。ログインユーザーと、その profile で使用する API token の所有者が一致することを確認する。

各 `login` command は agent が起動し、browser sign-in 中も command の終了を監視する。ユーザーへログイン完了の返信を求めない。`Signed in as '<expected-login>'` と exit code 0 を確認してから次の profile へ進み、保存先の browser state path を記録する。

## Step 4: Token を準備する

**PAT の入力を求める前に、現在の経路に必要な権限を classic / fine-grained の両方で提示する。** ユーザーに source / target の token type を一つずつ選んでもらい、必要な権限を準備できたことを確認してから `Read-Host` へ進む。

fine-grained PAT を選んだ token は URL status を `pending` にする。source / target organization login、host、fixture preparation、repository preparation mode が未確定なら、先に不足値を質問する。該当 token の完全な pre-filled URL を assistant 本文へ表示して検証し、status を `shown-and-validated` に更新するまで、次の操作を禁止する。

- 「必要な権限を準備できましたか」という質問
- `Read-Host` による PAT 入力
- preflight、fixture、export、GEI、import、verify

source と target の両方が fine-grained の場合は、**Source fine-grained PAT** と **Target fine-grained PAT** の見出しを付け、同じ assistant 本文に両方の clickable URL を表示する。permission の文章だけを列挙して URL を省略してはならない。URL を `ask_user.question` や `choices` に埋め込まない。

agent が対話 terminal を操作できる場合は、`Read-Host` command を同じ terminal instance へ agent が送信し、ユーザーには表示された prompt へ PAT 値だけを入力してもらう。agent が操作できない場合は、`Read-Host` command を質問カードより前の assistant 本文へ code block として掲載する。入力完了後は Step 4 の preflight から Step 10 まで、token を設定した同じ PowerShell terminal で command を実行する。agent の shell tool が別 process で動く場合は、token を必要とする command をその tool へ切り替えない。

mode ごとに必要な token だけを準備する。

- `read-only`: source Project を export できる source token だけ
- `api-only`: source export 用 token と target import / verify 用 token
- `browser-e2e`: browser profile と同じユーザーの source / target token

`setup --fixture` で organization repository を自動作成する完全自動経路では、確実性を優先する場合は classic PAT を推奨する。fine-grained PAT を選んだ場合は、下記の permission 設定だけで成功とみなさず、fixture 実行前に repository を作成しない preflight を必ず行う。

### Fine-grained PAT 作成 URL

ユーザーが fine-grained PAT を選んだ場合は、permission を手作業で列挙させるだけでなく、GitHub の [pre-filled fine-grained PAT URL](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#pre-filling-fine-grained-personal-access-token-details-using-url-parameters) を現在の経路に合わせて生成し、クリック可能な完全な URL として提示する。GitHub.com 側は `https://github.com/settings/personal-access-tokens/new`、data residency 側は `https://TENANT.ghe.com/settings/personal-access-tokens/new` を使う。`target_name` には確認済みの organization login を設定し、`name`、`description`、`expires_in=30` と次の permission query parameter を付ける。

| token / 経路 | 必須 query parameter | 条件付き query parameter |
|---|---|---|
| source: 既存 Project の export | `organization_projects=read`, `metadata=read` | organization Issue Field には `issue_fields=read`、private repository item には `issues=read`, `pull_requests=read` |
| source: `setup --fixture` + export | `administration=write`, `contents=write`, `issues=write`, `pull_requests=write`, `organization_projects=write`, `issue_fields=write`, `metadata=read` | なし |
| target: 既存または GEI 後 repository への import / verify | `organization_projects=write`, `metadata=read` | organization Issue Field の定義と値には `issue_fields=write`, `issues=write`、linked repository には `contents=write`、private repository item には `issues=read`, `pull_requests=read`、team collaborator には `members=read` |
| target: fixture seed + import / verify | `administration=write`, `contents=write`, `issues=write`, `pull_requests=write`, `organization_projects=write`, `issue_fields=write`, `metadata=read` | team collaborator には `members=read` |

すべての値を URL encode し、placeholder のまま提示しない。例:

```text
https://github.com/settings/personal-access-tokens/new?name=ghpmv-source-export&description=Export+an+organization+Project+with+ghpmv&target_name=octo-org&expires_in=30&organization_projects=read&metadata=read
```

表示前に token ごとに次を検証する。

1. URL host がその token の GitHub host と一致する。
2. `target_name` が確認済み organization login で、`TENANT`、`octo-org`、`<source-org>` などの placeholder が残っていない。
3. `name`、`description`、`target_name` が URL encode され、`expires_in=30` がある。
4. 現在の fixture / import 経路に必要な必須 permission parameter がすべてあり、値が `read` または `write` である。
5. source と target の URL を取り違えていない。

検証後、完全な raw URL を assistant 本文で一行の autolink として表示する。

```markdown
**Source fine-grained PAT**

<https://github.com/settings/personal-access-tokens/new?...>
```

URL 内に literal `\n`、escaped newline、空白、Markdown link label を混ぜない。renderer 上で折り返されても href 自体は一つの URL になるようにする。URL を表示した assistant 本文の直後に `ask_user` を呼ぶ場合、質問カードには「Source fine-grained PAT を準備できましたか？」のような確認文と choices だけを渡し、URL や Markdown を重複させない。

作成 URL では **Repository access** を指定できない。URL を開いた後、現在の経路に応じて参照される全 repository または fixture 用の **All repositories** をユーザー自身に選んでもらい、permission と expiration を確認してから生成する。organization approval が必要なら **Active** になるまで待つ。data residency token を GitHub.com の settings URL で作らせたり、GitHub.com token を tenant API に使わせたりしない。classic PAT と GEI token にはこの URL を使わず、scope と SSO authorization を従来どおり案内する。

### Classic PAT

| token / 経路 | 必要な scope |
|---|---|
| source: 既存 Project の export | `read:project`。private repository の item / linked repository を読む場合は `repo` も追加。 |
| source: `setup --fixture` + export | `repo`, `project`, `admin:org`。fixture が organization Issue Field を作成するため `admin:org` が必要。 |
| target: 既存または GEI 後 repository への import / verify | `project`, `read:org`。snapshot に organization Issue Field がある場合は `admin:org`、private target repository の item / linked repository 解決または Issue Field 値の書き込みには `repo` も追加。 |
| target: fixture seed + import / verify | `repo`, `project`, `admin:org`。 |

Organization が要求する場合は classic PAT を SSO authorize する。

### Fine-grained PAT

fine-grained PAT は organization-owned Project にだけ使用する。GitHub は user-owned Project へのアクセスを current limitation としているため、`--owner-type user` では classic PAT を選ぶ。

| token / 経路 | Resource owner / repository access | 必要な permission |
|---|---|---|
| source: 既存 Project の export | source Project の owner。参照される全 repository を選択。 | Organization **Projects: Read-only**。organization Issue Field がある場合は Organization **Issue Fields: Read-only**。Repository **Metadata: Read-only**。private repository item には **Issues: Read-only** と **Pull requests: Read-only**。 |
| source: `setup --fixture` + export | source organization。**All repositories**。 | Repository **Administration: Read and write**、**Contents: Read and write**、**Issues: Read and write**、**Pull requests: Read and write**。Organization **Projects: Read and write**、**Issue Fields: Read and write**。 |
| target: 既存または GEI 後 repository への import / verify | target Project の owner。mapping / Workflow が参照する全 target repository を選択。 | Organization **Projects: Read and write**。snapshot に organization Issue Field がある場合は Organization **Issue Fields: Read and write** と Repository **Issues: Read and write**。Repository **Metadata: Read-only**、linked repository には **Contents: Read and write**、private repository item には **Issues: Read-only** と **Pull requests: Read-only**。team collaborator を import する場合は Organization **Members: Read-only**。 |
| target: fixture seed + import / verify | target organization。**All repositories**。 | Repository **Administration: Read and write**、**Contents: Read and write**、**Issues: Read and write**、**Pull requests: Read and write**。Organization **Projects: Read and write**、**Issue Fields: Read and write**。team collaborator を import する場合は Organization **Members: Read-only**。 |

Organization が fine-grained PAT approval を要求する場合は承認済みであることを確認する。**既存 Project の export / import / verify だけを行うユーザーに fixture 作成用 permission を要求してはならない。**

GitHub の [fine-grained PAT permission matrix](https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens#organization-permissions-for-issue-fields) は、organization Issue Field の読み取りに **Issue Fields: read**、作成・更新・削除に **Issue Fields: write** を要求している。pre-filled URL の permission parameter は `issue_fields`。GitHub は Projects GraphQL mutation ごとの fine-grained PAT permission を公開していない。`linkProjectV2ToRepository` に対する Repository **Contents: Read and write** は実環境で確認した要件として案内し、PAT 向け公式要件として断定しない。

### GEI 専用 token

`repository preparation mode` が `GEI` の場合、GEI は fine-grained PAT を使用できないため、`SOURCE_TOKEN` / `TARGET_TOKEN` とは別に classic PAT を用意することを推奨する。

source / destination の role status が `migrator-pending` の間は、次の scope を説明してもよいが PAT の入力は求めない。Migrator ロールが適用されたことを確認して `migrator-active` に更新してから進める。

| GEI token | token owner の role | 必要な classic PAT scope |
|---|---|---|
| source | Organization owner または source organization の migrator | `admin:org`, `repo` |
| destination | Organization owner | `repo`, `admin:org`, `workflow` |
| destination | destination organization の migrator | `repo`, `read:org`, `workflow` |

同じ classic PAT を `ghpmv` と GEI で再利用する場合は、該当する scope の和集合が必要になる。不要な `admin:org` を `ghpmv` 専用 token に追加させない。

`read-only`:

```powershell
$sourceSecureToken = Read-Host "Source PAT" -AsSecureString
$env:SOURCE_TOKEN = [System.Net.NetworkCredential]::new("", $sourceSecureToken).Password
```

`api-only` と `browser-e2e`:

```powershell
$sourceSecureToken = Read-Host "Source PAT" -AsSecureString
$env:SOURCE_TOKEN = [System.Net.NetworkCredential]::new("", $sourceSecureToken).Password
$targetSecureToken = Read-Host "Target PAT" -AsSecureString
$env:TARGET_TOKEN = [System.Net.NetworkCredential]::new("", $targetSecureToken).Password
```

`GEI`:

```powershell
$geiSourceSecureToken = Read-Host "GEI source classic PAT" -AsSecureString
$env:GEI_SOURCE_TOKEN = [System.Net.NetworkCredential]::new("", $geiSourceSecureToken).Password
$geiTargetSecureToken = Read-Host "GEI target classic PAT" -AsSecureString
$env:GEI_TARGET_TOKEN = [System.Net.NetworkCredential]::new("", $geiTargetSecureToken).Password
```

### Fine-grained fixture token の preflight

`fixture preparation` が `create` で source に fine-grained PAT を選んだ場合、`setup --fixture` より先に次を実行する。target の `fixture-seed` でも organization と token を置き換えて同じ確認を行う。

```powershell
$previousGhToken = $env:GH_TOKEN
$env:GH_TOKEN = $env:SOURCE_TOKEN
try {
    gh api --include --method POST "orgs/<source-org>/repos"
    gh api --include --method POST "orgs/<source-org>/issue-fields"
}
finally {
    $env:GH_TOKEN = $previousGhToken
}
```

data residency 側を確認するときは、両方の `gh api` command に `--hostname TENANT.ghe.com` を追加する。GitHub.com source → data residency target の source preflight には追加せず、target preflight だけに target tenant hostname を追加する。

どちらも必須 field を渡さないため repository や Issue Field は作成されない。両方が `422 Validation Failed` なら endpoint permission は認識されているため続行できる。repository endpoint が `403 Resource not accessible by personal access token` なら、設定画面で **Administration: Read and write**、**All repositories**、organization approval を再確認する。Issue Field endpoint または GraphQL の `organization.issueFields` が `FORBIDDEN` なら **Organization permissions → Issue Fields: Read and write** (`issue_fields=write`) を確認する。token owner の organization role、member の repository creation policy、organization の PAT restriction も別に確認する。原因を一つに断定しない。再作成しても 403 の場合は `setup --fixture` を実行せず、次のどちらかを選んでもらう。

1. classic PAT (`repo`, `project`, `admin:org`) に切り替える
2. 空の private repository を先に作り、fine-grained PAT で残りの fixture を作成する

fine-grained PAT の **Administration** または **All repositories** を付与できない場合だけ、空 repository を先に作成し、その repository を選択した token に Administration 以外の fixture 権限を付ける。

## Step 5: Source fixture

`api-only` または `browser-e2e` で `fixture preparation` が `create` の場合だけ実行する。`read-only` と `existing` の経路ではスキップし、source resource を作成しない。

source organization、衝突しない fixture title / repository name を一つずつ確認する。作成物を説明してから実行する。

```powershell
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- setup `
  --fixture `
  --fixture-org <source-org> `
  --fixture-title <unique-title> `
  --fixture-repo <unique-repo> `
  --token $env:SOURCE_TOKEN
```

source が data residency の場合は `--api-base-url <source-api-url>` を追加する。`browser-e2e` の `--fixture-ui` にはさらに `--browser-base-url <source-web-url>` を追加する。GitHub.com source ではどちらも付けない。

出力された source Project number を記録する。

`browser-e2e` では続けて UI fixture を適用する。

```powershell
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- setup `
  --fixture-ui `
  --fixture-org <source-org> `
  --fixture-project <source-project-number> `
  --fixture-repo <source-repo> `
  --browser-profile source `
  --token $env:SOURCE_TOKEN
```

### Fixture UI 再実行

同じ Project に明示的に再実行すると non-default Views が重複する。次のどちらかを選んでもらう。

1. 新しい fixture Project を作る（推奨）
2. `View 1` を残し、既存の `Fixture Board` / `Fixture Roadmap` を手動削除して再実行する

Workflow は再設定できる。warning が出た場合は、目視だけで終了せず、後続 export が UI settings を警告なしで取得できるか確認する。

## Step 6: Source export

再実行時は新しい directory を使う。mapping CSV は既存ファイルを上書きしない。

`read-only` と `api-only` では browser option を付けない。

```powershell
$env:GHPMV_DEMO_SNAPSHOT = Join-Path $env:TEMP "ghpmv-demo-snapshot-$(Get-Date -Format yyyyMMdd-HHmmss)"
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- export `
  --org <source-org> `
  --project <source-project-number> `
  --out $env:GHPMV_DEMO_SNAPSHOT `
  --token $env:SOURCE_TOKEN
```

`browser-e2e` では同じ export に browser option を追加する。

```powershell
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- export `
  --org <source-org> `
  --project <source-project-number> `
  --out $env:GHPMV_DEMO_SNAPSHOT `
  --token $env:SOURCE_TOKEN `
  --enable-browser-automation `
  --browser-profile source
```

source が data residency の場合は、browser option の有無にかかわらず `--base-url <source-api-url>` を追加し、browser automation を使う場合はさらに `--browser-base-url <source-web-url>` を追加する。`github.com-to-ghec-dr` の source export にはこれらを付けない。

確認するもの:

- `snapshot.json`
- `repository-mappings.csv`
- `organization-mappings.csv`
- 必要な場合 `user-mappings.csv`
- View / Workflow / collaborator warning

warning がある場合、どの UI-only field が欠落したかを示して続行可否を確認する。

`read-only` はここで完了報告を行い、終了する。target resource の準備、mapping の編集、import、verify は案内しない。

## Step 7: Target repository を準備する

`api-only` と `browser-e2e` だけが実行する。

Step 1 で記録した `repository preparation mode` の経路だけを実行する。

### GEI

`docs/MANUAL_TEST_PLAN.md` の §6 に従い、`GEI_SOURCE_TOKEN` / `GEI_TARGET_TOKEN` で repository migration を完了する。destination の ruleset がある場合、**Repository migrations** bypass を **Exempt** にする。既定の **Always allow** のまま進めない。

target が data residency の場合は `gh gei migrate-repo --help` で extension の現在の引数を確認し、migration command に `--target-api-url <target-api-url>` を追加する。GitHub の [Migrating repositories from GitHub.com to GitHub Enterprise Cloud](https://docs.github.com/en/migrations/using-github-enterprise-importer/migrating-between-github-products/migrating-repositories-from-githubcom-to-github-enterprise-cloud) と data residency の手順に従い、destination organization / enterprise がその tenant に向いていること、tenant 固有の IP allow list を確認する。`github.com-to-ghec-dr` では source endpoint は GitHub.com のまま、target endpoint だけを `https://api.TENANT.ghe.com` にする。

target repository full name を記録し、target の Issue / PR number が source と一致することを確認する。downloadable migration log は完了後 24 時間以内に保存する。target repository の Issues が無効なら `Migration Log` Issue は作成されない。migration 成功と number 維持を確認できるまで Step 8 へ進まない。

### Fixture seed

`ghpmv` 自体の短時間デモ用であり、GEI の検証にはならず、補助 Project が一つ増えることを説明してから実行する。

```powershell
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- setup `
  --fixture `
  --fixture-org <target-org> `
  --fixture-title <unique-target-seed-title> `
  --fixture-repo <target-repo> `
  --token $env:TARGET_TOKEN
```

target が data residency の場合は `--api-base-url <target-api-url>` を追加する。

target 側の `setup --fixture-ui` は不要。

## Step 8: Mapping を完成させる

`api-only` と `browser-e2e` だけが実行する。

生成済み CSV を必ず読み、空の target 値を列挙する。固定の一行だけで置き換えない。

- `repository-mappings.csv`: full name と short name の候補を含め、全行を target `owner/repo` へ対応付ける。
- `organization-mappings.csv`: source owner を target owner へ対応付ける。
- `user-mappings.csv`: source login / mannequin user を実 target login へ対応付ける。

target login は token 値ではなくユーザー名だけを確認する。EMU suffix を省略しない。編集後に CSV を再読し、空の target 値がないことを確認する。

## Step 9: Import

`api-only` と `browser-e2e` だけが実行する。

存在する mapping file をすべて渡す。

`api-only` では browser option を付けない。

```powershell
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- import `
  --org <target-org> `
  --in $env:GHPMV_DEMO_SNAPSHOT `
  --token $env:TARGET_TOKEN `
  --repo-mapping "$env:GHPMV_DEMO_SNAPSHOT\repository-mappings.csv" `
  --user-mapping "$env:GHPMV_DEMO_SNAPSHOT\user-mappings.csv" `
  --org-mapping "$env:GHPMV_DEMO_SNAPSHOT\organization-mappings.csv"
```

target が data residency の場合は `--target-base-url <target-api-url>` を追加する。GitHub.com target では付けない。

`browser-e2e` では同じ import に browser option を追加する。

```powershell
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- import `
  --org <target-org> `
  --in $env:GHPMV_DEMO_SNAPSHOT `
  --token $env:TARGET_TOKEN `
  --repo-mapping "$env:GHPMV_DEMO_SNAPSHOT\repository-mappings.csv" `
  --user-mapping "$env:GHPMV_DEMO_SNAPSHOT\user-mappings.csv" `
  --org-mapping "$env:GHPMV_DEMO_SNAPSHOT\organization-mappings.csv" `
  --enable-browser-automation `
  --browser-profile target
```

target が data residency の場合は `--target-base-url <target-api-url>` と `--browser-base-url <target-web-url>` を追加する。`github.com-to-ghec-dr` ではこの target command にだけ両方を付ける。

生成されなかった optional mapping file の引数だけを外す。出力の `result` と target Project number を記録する。

## Step 10: Verify

`api-only` と `browser-e2e` だけが実行する。

Import と同じ mapping / browser profile を渡す。

`api-only` では browser option を付けない。

```powershell
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- verify `
  --org <target-org> `
  --project <target-project-number> `
  --in $env:GHPMV_DEMO_SNAPSHOT `
  --token $env:TARGET_TOKEN `
  --repo-mapping "$env:GHPMV_DEMO_SNAPSHOT\repository-mappings.csv" `
  --user-mapping "$env:GHPMV_DEMO_SNAPSHOT\user-mappings.csv" `
  --org-mapping "$env:GHPMV_DEMO_SNAPSHOT\organization-mappings.csv" `
  --report-json "$env:GHPMV_DEMO_SNAPSHOT\verify-report.json"
```

target が data residency の場合は `--target-base-url <target-api-url>` を追加する。

`browser-e2e` では同じ verify に browser option を追加する。

```powershell
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- verify `
  --org <target-org> `
  --project <target-project-number> `
  --in $env:GHPMV_DEMO_SNAPSHOT `
  --token $env:TARGET_TOKEN `
  --repo-mapping "$env:GHPMV_DEMO_SNAPSHOT\repository-mappings.csv" `
  --user-mapping "$env:GHPMV_DEMO_SNAPSHOT\user-mappings.csv" `
  --org-mapping "$env:GHPMV_DEMO_SNAPSHOT\organization-mappings.csv" `
  --enable-browser-automation `
  --browser-profile target `
  --report-json "$env:GHPMV_DEMO_SNAPSHOT\verify-report.json"
```

target が data residency の場合は `--target-base-url <target-api-url>` と `--browser-base-url <target-web-url>` を追加する。Import と Verify で同じ target endpoint と browser profile を使う。

結果を category ごとに確認する。

- `Match`: 成功
- `PartialMatch`: warning の内容と許容理由を記録
- `Mismatch`: 差分を直して再検証
- `NotVerified`: 必要データが capture できていないため成功扱いにしない

## Troubleshooting

| エラー / 症状 | 対応 |
|---|---|
| fine-grained PAT preflight / `setup --fixture` で `Resource not accessible by personal access token` | repository endpoint なら **Administration: Read and write** と **All repositories**、Issue Field endpoint または GraphQL `organization.issueFields` なら Organization **Issue Fields: Read and write** (`issue_fields=write`) を確認する。organization approval、token owner の role、repository creation / PAT policy、SSO も確認し、原因を一つに断定しない。解決できなければ repository を先に作成するか classic PAT (`repo`, `project`, `admin:org`) へ切り替える。 |
| `INSUFFICIENT_SCOPES`, `id`, `read:org` | classic PAT に `read:org` を追加し、必要なら SSO を再承認する。 |
| `The browser session is not signed in to 'github.com'` | 該当 profile で `login` を再実行し、API token と同じユーザーでログインする。 |
| `The browser session is not signed in to '<tenant>.ghe.com'` または host mismatch | target profile を `login --profile target --base-url https://TENANT.ghe.com` で作り直し、Import / Verify に `--target-base-url https://api.TENANT.ghe.com` と `--browser-base-url https://TENANT.ghe.com` を渡す。GitHub.com source profile / token と tenant target profile / token を混用しない。 |
| `Viewer not authorized to change project visibility` | target Project の現在値と snapshot の visibility を確認する。差分がある場合は、organization owner または visibility 変更を許可された organization role の token owner を使う。値が同じなのに発生した場合は、no-op visibility mutation を省略する版の `ghpmv` で再実行する。 |
| `linkProjectV2ToRepository` で `Resource not accessible by personal access token` | 実環境で確認した対処として、target fine-grained PAT で対象 repository を選択し、Repository **Contents: Read and write** を追加する。permission 変更後に organization approval が **Active** であることも確認する。GitHub はこの mutation の PAT permission を個別には文書化していない。 |
| Collaborator が `NotVerified`、`Manage access` 待機が timeout、または `/settings/access` が 404 | target browser/token user が Project の **Settings → Manage access** を開けるか確認する。開けない member profile ではなく、同じ login の organization-owner または十分な project-admin token / browser profile で verify を再実行する。 |
| UI fixture 再実行で View が重複 | 新規 fixture を使うか、`View 1` 以外の fixture Views を削除してから再実行する。 |
| mapping 不足 | 生成された全 CSV を再読し、空 target と short-name 候補を補完する。 |
| Issue / PR item skip | target repository の可視性、mapping、Issue / PR number 維持を確認する。 |
| Workflow warning | Auto-add 上限、repository visibility、filter mapping、現在の UI selector を確認し、browser export で capture 可否を再確認する。 |

## 完了報告

最後に次だけを簡潔に報告する。

- build / deterministic test 結果
- source / target Project URL または番号
- export / import result
- verify overall / category result
- 許容した warning
- 作成した一時リソースと snapshot directory

cleanup はユーザーが明示的に希望した場合だけ、`docs/MANUAL_TEST_PLAN.md` の手順で行う。PR、commit、push は別途依頼されるまで行わない。

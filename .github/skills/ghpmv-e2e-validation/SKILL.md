---
name: ghpmv-e2e-validation
description: ghpmv の実環境動作確認を、ビルド、Playwright準備、source/target fixture、browser profile、export、mapping、import、verifyまで一問一答で安全に案内する。「動作確認したい」「ステップバイステップでガイド」「実環境で試したい」「fixtureを作って移行テスト」「browser automationを含めて検証」「Field sumをE2E検証」「E2E migration test」などの依頼で使用する。
---

# ghpmv E2E Validation

`ghpmv` の GitHub.com / GHEC 実環境テストを、一度に一段だけ案内する。最終目標は browser automation を含む `export` → `import` → `verify` を完了し、`Match`、または説明可能な `PartialMatch` を得ること。

詳細仕様と手動チェック項目は次を参照する。

- `README.md` の Token permissions と browser automation
- `docs/MANUAL_TEST_PLAN.md`
- `.github/copilot-instructions.md` の build / test command

## 最重要原則

1. **一度に一つのステップだけ案内する。** コマンドを提示したら結果を確認し、成功するまで次へ進まない。
2. **必要な質問だけを一つずつ、必ず対話用質問ツールで行う。** 選択式では choices を付ける。validation run が新規作成する Project / repository 名は run ID から安全に自動生成し、質問しない。推奨値を安全に決められない login、organization、既存 repository 名などだけ choices なしの質問カードを使う。command の終了、exit code、出力、生成ファイルなど agent が観測できる事実をユーザーへ質問してはならない。
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
14. **遷移説明だけで停止しない。** 質問への回答で現在の Step に必要な値と同意がすべて揃った場合は、「準備できました」「次へ進めます」だけを返して turn を終了してはならない。同じ turn で次の必要な terminal command を送信し、その完了監視まで開始する。追加の user decision、secret 入力、warning 承認、削除同意が必要な場合だけ質問で停止する。
15. **内部 ID だけの選択肢を出さない。** `build-only`、`api-only` などの記録用 ID を choice に単独表示せず、各 choice に「何を実行するか」「実 resource を読み書きするか」を日本語で含める。ユーザーの依頼から推奨できる choice には `(Recommended)` を付ける。選択後は説明文ではなく対応する内部 ID を state に記録する。
16. **Skill を実行する project session が terminal を所有する。** 親・兄弟 session で開いた terminal が画面に見えていても再利用しない。すでに検証用 nested session 内で Skill が起動された場合は、さらに nested session を作成しない。
17. **in-flight command を再送しない。** command の sentinel が未到達なら実行中として扱い、同じ command、readiness probe、状態確認 command、後続 commandを terminal へ送らない。空出力や一時的な terminal transport error は再送理由にならない。
18. **現在の入力だけを案内する。** Browser sign-in 中に、後続 Step の PAT や token 入力を予告しない。ユーザーが今操作すべき browser または terminal と、その操作内容だけを平易な言葉で示す。
19. **terminal canvas と shell tool を混同しない。** readiness を確認した `token execution terminal` への入力は、その terminal instance の `send_terminal_input` action だけで行う。`powershell`、`task`、別 process の shell tool で同じ command を実行してはならない。terminal canvas を開いて出力を読めた時点で「agent が terminal に直接入力できる」と扱い、command を本文へ貼ってユーザーに再実行させる fallback へ切り替えない。
20. **`hidden prompt` という語をユーザーへ表示しない。** PAT 入力時は「右側のターミナルに PAT 入力欄を表示します。入力中の文字は画面に表示されません」と説明する。内部 marker、sentinel、env var の実装説明ではなく、いま入力する PAT の用途と Enter を押す操作だけを案内する。
21. **Browser login の完了を自動監視する。** login command 送信後は同じ terminal output を5〜10秒間隔で読み、今回の sentinel または5分 timeoutまで監視を継続する。sentinel未到達のまま「待機中です」と返してturnを終了したり、`したよ`などの返信をユーザーへ要求したりしない。

## 実行 session と terminal ownership

Skill を起動した現在の project session を `validation execution session` として記録し、この session 自身が全質問、terminal action、command 監視を行う。親 session が検証用 child session を作る場合は一つだけ作り、kickoff prompt には「この session で workflow を実行する」と書く。「新しい nested session を作って実行する」と child に再委任してはならない。

Skill 自体の変更を検証する場合は、変更後の current branch HEAD から新しい child session を作る。変更前に作成した既存 child session は古い Skill を読み込んでいるため再利用しない。

terminal canvas の instance ID だけでなく、利用可能なら owning project session / provider も記録する。terminal panel がユーザーに表示されていることは、現在の agent がその terminal を操作・観測できる証拠ではない。owner が `validation execution session` と一致しない terminal は `token execution terminal` に採用せず、Step 2 より前に現在の session が新しい terminal を一つだけ開く。

## 自動完了検出

対話 terminal へ送る非対話 command は、送信直前に agent が command ごとの一意な `<command-id>` を生成し、可能な限り次の形で完了 sentinel と exit code を出す。`<command>` 自体の出力だけを見て早期に成功判定しない。一つの wrapper には native command を一つだけ入れる。restore と build など複数の native command は別々に送り、前の command の成功を確認するまで次を送らない。

`send_terminal_input` へ渡す直前に、次の読みやすい複数行例を **改行を含まない一つの PowerShell statement line** へ直列化する。terminal action に複数行の text を渡してはならない。複数行 paste は行の実行順序を保証せず、完了 sentinel が native command より先に実行されることがある。

```powershell
$global:LASTEXITCODE = 0
& {
    <command>
}
$ghpmvInvocationSucceeded = $?
$ghpmvNativeExitCode = $LASTEXITCODE
$ghpmvExitCode = if ($ghpmvInvocationSucceeded -and $ghpmvNativeExitCode -eq 0) { 0 } elseif ($ghpmvNativeExitCode -ne 0) { $ghpmvNativeExitCode } else { 1 }
Write-Output "GHPMV_COMMAND_DONE:<command-id>:$ghpmvExitCode"
```

実際の terminal input は次の一行形式にする。

```powershell
$global:LASTEXITCODE = 0; & { <command> }; $ghpmvInvocationSucceeded = $?; $ghpmvNativeExitCode = $LASTEXITCODE; $ghpmvExitCode = if ($ghpmvInvocationSucceeded -and $ghpmvNativeExitCode -eq 0) { 0 } elseif ($ghpmvNativeExitCode -ne 0) { $ghpmvNativeExitCode } else { 1 }; Write-Output "GHPMV_COMMAND_DONE:<command-id>:$ghpmvExitCode"
```

送信直前に `in-flight command` として terminal instance ID、`<command-id>`、command の目的または hash、期待 sentinel を記録する。一つの terminal で同時に持てる in-flight command は一つだけとし、未解決の記録がある間は `send_terminal_input` を再度呼ばない。

terminal 出力取得 action で、今回送信した `<command-id>` と完全一致する `GHPMV_COMMAND_DONE:<command-id>:0` を読めた場合だけ成功とする。過去の command の sentinel を再利用しない。まだ今回の sentinel がなければ command 実行中として同じ instance の出力監視だけを継続し、ユーザーへ完了報告を求めず、command を再送しない。sentinel を確認した場合だけ in-flight state を clear して次の command へ進む。platform が process completion notification を提供する shell tool を使える非 secret command は、その通知と exit code を利用してよい。

session の idle / interruption から復帰した場合は、新しい input を送る前に同じ terminal instance の full scrollback または十分な tail を読み、記録済み sentinel を検索する。`since_last_input` が空、画面が変化していない、または sentinel がまだないことだけで command 消失と判断しない。terminal process が確実に終了したかを観測できない状態では再実行せず、transport recovery を優先する。

browser login command も同様に agent が終了まで監視する。ユーザーには「開いた browser で `<expected-login>` として sign in してください」と現在の browser 操作だけを通知し、質問カードや「完了したら返答」を表示しない。この通知に PAT、token、後続 Step の準備を混ぜない。送信後は5〜10秒間隔で同じ terminal output を読み、command の `Signed in as '<reported-login>'` から login を取り出し、`<expected-login>` と大文字小文字を区別せず一致し、かつ exit code 0 になったことをagent自身が確認して次へ進む。まだ出力が変化していない場合も5分timeoutまでは監視を継続し、ユーザー返信待ちへ切り替えない。timeout、account mismatch、SSO failure の場合だけエラーを説明して再試行方法を質問する。

| Step / 処理 | agent が自動確認するもの |
|---|---|
| terminal readiness | `GHPMV_TERMINAL_READY` |
| restore / build / browser setup | exit code または完了 sentinel |
| browser login | 出力された login と期待 login の case-insensitive 一致、および exit code 0 |
| PAT permission preflight | HTTP status と endpoint ごとの response |
| fixture 作成 | exit code、作成された repository / Project、Project number |
| export | exit code、`snapshot.json`、mapping CSV、warning |
| browser-e2e field sums | snapshot の 4 View contract、target `View: Match`、drift report、repair report |
| GEI | migration status、target repository、Issue / PR number |
| import | `result`、target Project number、`import-log.json` |
| verify | overall / category result、`verify-report.json` |
| cleanup | resource inventory と明示同意、削除した各 resource の read-back |

対話用質問ツールを使うのは、validation mode、host / organization / login / resource name、mapping の未知値、PAT の terminal 手入力、warning の許容、cleanup 同意など、ユーザーの判断または agent が観測できない入力が必要な場合に限る。

## 対話 terminal の readiness gate

`read-only`、`api-only`、`browser-e2e` では、Step 1 で validation mode と経路を確定した直後、Step 2 より前にユーザーと agent の両方が操作できる PowerShell terminal を一つ開く。`build-only` と `baseline-full` では対話 terminal を要求しない。

terminal canvas の open input に `command` がある場合は、空の canvas を開いて直後に `send_terminal_input` するのではなく、新しい一意な instance ID を使い、readiness command 付きで atomic に open する。panel は focus する。

```powershell
Write-Output "GHPMV_TERMINAL_READY"
```

open 後の terminal process 起動は非同期である。最初の出力取得が空でも失敗扱いせず、同じ instance を再読する。`GHPMV_TERMINAL_READY` を実際に読めた場合だけ terminal を ready と記録して Step 2 へ進む。canvas を開く action が成功しただけでは ready とみなさない。

readiness 中で、まだ token も in-flight command もない terminal に `Terminal not found or not running` が返った場合だけ、stale instance を破棄し、新しい一意な instance ID で command 付き open を bounded retry する。空出力または一時的な runtime error だけを理由に project session を作り直さない。

`provider not connected`、`cannot be reached` などの transport / ownership error は terminal process の終了と同一視しない。まず現在の project session と terminal owner が一致するか確認する。owner mismatch ならその terminal への read / send を止め、readiness gate 内で現在の session 所有の terminal を一つだけ開く。owner が一致するのに provider が一時切断されている場合は、既存 panel の focus や App connection の回復を案内して停止し、fresh terminal を連続作成しない。

token 設定後または in-flight command 送信後に terminal が到達不能になった場合は、実行中 command を別 terminal で再送しない。既存 terminal の recovery が不可能と確定した場合だけ新しい terminal で readiness gate から再開し、必要な token の missing check と hidden re-entry を完了するまで後続 command を送らない。fresh instance でも readiness が繰り返し失敗した場合は停止し、terminal panel の focus / App 再起動を案内する。成功するまで build、test、browser setup、token、live resource の処理を一切実行しない。

ready になった terminal instance ID を `token execution terminal` として記録し、`read-only`、`api-only`、`browser-e2e` の Step 2 以降の command はすべてその terminal へ送信する。別 process の shell tool へ切り替えない。

token 入力時は次の流れを必須とする。

1. token prompt は **一度に一つだけ**送信する。source / target / GEI source / GEI target の複数 `Read-Host` を同じ `send_terminal_input` call や同じ PowerShell block に入れない。
2. prompt label には env var、organization、host、用途をすべて入れる。`Source PAT` / `Target PAT` や「1個目 / 2個目」だけで区別しない。
3. token prompt を送る直前に一意な `<token-prompt-id>` を生成して記録する。`Read-Host`、環境変数代入、`GHPMV_<token>_(READY|MISSING):<token-prompt-id>` sentinel を一行の PowerShell command として送信する。複数行 paste による順序反転を避ける。
   - token prompt は対話 command なので、上記の汎用 `GHPMV_COMMAND_DONE` wrapper、`$global:LASTEXITCODE`、`$ghpmvExitCode` を付けない。
   - `<token-prompt-id>` は command 内へ解決済みの固定文字列として直接埋め込む。`$tokenPromptId` のような PowerShell variable にせず、`"$variable:$otherVariable"` 形式の colon interpolation を作らない。
   - readiness 済み terminal instance の `send_terminal_input` action へこの一行を一度だけ送る。別 process の shell toolへ渡してはならない。
4. terminal が secret 入力待ちになったことを確認し、ユーザーに **terminal 上で該当する PAT 値だけを手入力**してもらう。PAT を会話、質問カード、terminal action の引数へ貼らせない。
5. terminal canvas は secret 入力完了時に agent を自動 wake しないため、この操作だけは `ask_user` で入力完了を確認する。質問カードには、右側 terminal に表示済みの PAT 入力欄へ、現在対象の organization / host / 用途の PAT を入力して Enter を押すことと、入力中の文字は表示されないことを平易に明記する。sentinel や marker はユーザーへ説明しない。
6. 同じ token について待機メッセージ、出力 read、質問カードを繰り返さない。command を一度送信したら確認カードを一枚だけ表示し、ユーザーの応答を待つ。
7. ユーザーの応答後、token 値を表示せず、現在記録している `<token-prompt-id>` と完全一致する readiness sentinel を確認する。scrollback 内の過去の固定 marker は成功扱いしない。確認できた場合だけ次の token prompt へ進む。sentinel がなければ一度だけ状態を説明し、その token だけを再入力するか確認する。
8. すべての token が ready になった後、agent が同じ terminal instance へ preflight / fixture / export / GEI / import / verify command を送信する。token を参照する command を、別 process で動く shell tool へ切り替えない。

token 入力中または直後に session が idle / interrupted になった場合は、再入力を求める前に一意な `<recovery-id>` を生成し、同じ terminal instance で環境変数の有無だけを確認する。token 値や長さは表示せず、今回の `<recovery-id>` と完全一致する marker だけを採用する。

```powershell
if ([string]::IsNullOrWhiteSpace($env:SOURCE_TOKEN)) { Write-Output "GHPMV_SOURCE_TOKEN_MISSING:<recovery-id>" } else { Write-Output "GHPMV_SOURCE_TOKEN_READY:<recovery-id>" }
if ([string]::IsNullOrWhiteSpace($env:TARGET_TOKEN)) { Write-Output "GHPMV_TARGET_TOKEN_MISSING:<recovery-id>" } else { Write-Output "GHPMV_TARGET_TOKEN_READY:<recovery-id>" }
if ([string]::IsNullOrWhiteSpace($env:GHPMV_GEI_SOURCE_TOKEN)) { Write-Output "GHPMV_GEI_SOURCE_TOKEN_MISSING:<recovery-id>" } else { Write-Output "GHPMV_GEI_SOURCE_TOKEN_READY:<recovery-id>" }
if ([string]::IsNullOrWhiteSpace($env:GHPMV_GEI_TARGET_TOKEN)) { Write-Output "GHPMV_GEI_TARGET_TOKEN_MISSING:<recovery-id>" } else { Write-Output "GHPMV_GEI_TARGET_TOKEN_READY:<recovery-id>" }
```

選択済み経路で必要な token がすべて ready なら再入力させず次へ進む。`read-only` では `SOURCE_TOKEN`、`api-only` / `browser-e2e` では `SOURCE_TOKEN` と `TARGET_TOKEN`、さらに `repository preparation mode` が `GEI` の場合は `GHPMV_GEI_SOURCE_TOKEN` と `GHPMV_GEI_TARGET_TOKEN` も必須とする。選択経路で不要な token の missing は blocker にしない。必要な token のうち missing のものだけを、上記の一 token 一 command の手順で再入力する。

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
| source / target empty-repository fallback | side ごとの `selected` または `not-selected` |
| GEI source / destination role status | `owner`, `migrator-active`, `migrator-pending` |
| validation mode | `build-only`, `baseline-full`, `read-only`, `api-only`, `browser-e2e` |
| fixture preparation | `existing` または `create` |
| source / target token type | `classic` または `fine-grained` |
| source / target fine-grained PAT URL status | `not-required`, `pending`, `shown-and-validated` |
| validation execution session | Skill と terminal workflow を所有する現在の project session |
| token execution terminal | token を設定し、以後の live command を実行する同一 PowerShell session |
| terminal owner / provider | `validation execution session` と一致する canvas owner / provider |
| in-flight command | terminal instance、command ID、目的または hash、期待 sentinel |
| required token inventory | 選択経路で必要な env var、host、owner、type、role、scope / permission、作成 URL status |
| source host type / web URL / API URL | `github.com`, `https://github.com`, `https://api.github.com/graphql` |
| target host type / web URL / API / uploads URL | `ghec-dr`, `https://TENANT.ghe.com`, `https://api.TENANT.ghe.com`, `https://uploads.TENANT.ghe.com` |
| host topology | `github.com-to-github.com`, `github.com-to-ghec-dr` など |
| browser-e2e field-sum contract | 下記の View / field 名と期待値 |
| browser-e2e field-sum status | `fixture-pending`, `snapshot-match`, `target-view-match`, `target-render-observed`, `drift-detected`, `repair-match` |
| resource inventory | この run が作成した Project / repository の side、name、URL / number、作成 Step、cleanup 状態 |

`browser-e2e` の既存 round-trip は次の field-sum contract も常に検証する。別 scenario には分岐させず、settings に重複保存しない。

| View | Layout / grouping | expected `FieldSum` |
|---|---|---|
| `View 1` | `TABLE_LAYOUT` / `Status` | `Count`, `Fixture Number`, `Fixture Number 2` |
| `Fixture Roadmap` | `ROADMAP_LAYOUT` / `Status` | `Fixture Number 2` |
| `Fixture Board` | `BOARD_LAYOUT` / `Status` | `Fixture Number` |
| `Fixture Empty Sums` | `TABLE_LAYOUT` / `Status` | empty |

required Number fields は `Fixture Number` と `Fixture Number 2`。source / target の実 resource 名を E2E settings schema に追加する必要はない。browser state、PAT、cookie は引き続き settings に保存しない。

## Feature checkpoint の実行時間最小化

Issue ごとの機能検証を追加するときも、user-facing scenario selector や独立した full round trip を増やさない。次へ統合する。

- fixture 作成・期待値記録: 既存 Step 5
- `snapshot.json` / mapping の追加 assertion: 既存 Step 6 の同じ export 結果
- target import assertion: 既存 Step 9 の同じ target Project
- category Match: 既存 Step 10 の browser-assisted verify
- deliberate drift: 既存 target 上の negative-test phase
- repair: Status Updates、View order、Team link などの idempotence を確認する同じ `--project-number` re-import
- 最終 verify、証跡、cleanup: 既存 report、resource inventory、cleanup consent

追加の disposable target または native command は、fresh/existing、REST/browser、権限境界、destructive preview など、既存 command では別 code path を証明できない場合だけ許可する。追加理由と検証対象を明記し、resource inventory と cleanup に含める。同じ snapshot、mapping、target、verify command を再利用できる場合は複製しない。

## Resource inventory と cleanup

実 resource を作成する command の成功直後に、次を `resource inventory` へ追加する。既存 resource は `pre-existing` として参照記録だけを残し、cleanup 対象にしない。

| 作成 Step | inventory entry |
|---|---|
| Step 5 `setup --fixture` | source Project（title / number / URL）と source repository（owner/name / URL） |
| Step 7 GEI | target repository（owner/name / URL） |
| Step 7 fixture seed | target seed Project（title / number / URL）と target repository（owner/name / URL） |
| Step 9 import | imported target Project（title / number / URL） |

Project 内の Views / Workflows は親 Project の nested resource として同じ entry に記録する。各 entry は `created`, `retained`, `deleted` の cleanup 状態に加え、owner type、host、cleanup に使う token environment variable、token type、削除 permission の確認状態を持つ。command が失敗した場合も部分作成を確認し、作成済み resource があれば inventory へ追加する。

Step 10 と `browser-e2e` の field-sum drift / repair が完了した場合だけでなく、最初の `created` entry 記録後に fixture、GEI、mapping、import、verify、drift、repair のいずれかが失敗した場合も、終了前に cleanup consent へ遷移する。失敗内容を示した後、cleanup 対象の `created` entry を name / URL / number 付きで一覧表示し、対話用質問ツールで一度だけ明示的な同意を確認する。Cancel / Skipped は delete command を送らず全 entry を `retained` として pause する。選択肢は次のように resource への影響を含める。

1. `この run が作成した一覧内の Project / repository をすべて削除する`
2. `target 側の一時 resource だけ削除し、source fixture は再利用のため残す`
3. `一時 resource をすべて残し、削除せず URL を完了報告へ記録する`

同意前に delete command を送らない。削除を選んだ場合は選択範囲を reverse creation order で一 resource ずつ削除し、各 command の sentinel / exit code と read-back を確認して `deleted` へ更新する。削除対象の title / owner / name / number が inventory と一致しなければ停止する。残す entry は `retained` とし、後から削除できるよう URL を報告する。

### Project cleanup commands

削除同意後、Project ごとに次の 3 command を別々の一意な command ID で送る。placeholder は inventory の実値へ置き換える。`<cleanup-token-env-var>` は side に対応する ghpmv token、`<cleanup-host>` は `github.com` または tenant host、`<owner-type>` は `organization` / `user`。

1. **照合して node ID を保持する**

```powershell
function Stop-ProjectCleanup([string]$Message) { Write-Error $Message; $global:LASTEXITCODE = 1 }
$cleanupToken = [Environment]::GetEnvironmentVariable('<cleanup-token-env-var>')
if ([string]::IsNullOrWhiteSpace($cleanupToken)) { Stop-ProjectCleanup 'Cleanup token is not available in the token execution terminal.'; return }
$cleanupHostArguments = if ('<cleanup-host>' -eq 'github.com') { @() } else { @('--hostname', '<cleanup-host>') }
$cleanupQuery = if ('<owner-type>' -eq 'organization') {
    'query($login:String!,$number:Int!){organization(login:$login){projectV2(number:$number){id number title url}}}'
} else {
    'query($login:String!,$number:Int!){user(login:$login){projectV2(number:$number){id number title url}}}'
}
$previousCleanupToken = $env:GH_TOKEN
try {
    $env:GH_TOKEN = $cleanupToken
    $cleanupResponseText = gh api @cleanupHostArguments graphql -f query=$cleanupQuery -f login='<owner-login>' -F number=<project-number>
    if ($LASTEXITCODE -ne 0) { Stop-ProjectCleanup 'Project cleanup lookup failed.'; return }
}
finally {
    if ($null -eq $previousCleanupToken) { Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue } else { $env:GH_TOKEN = $previousCleanupToken }
}
$cleanupResponse = $cleanupResponseText | ConvertFrom-Json
$cleanupProject = if ('<owner-type>' -eq 'organization') { $cleanupResponse.data.organization.projectV2 } else { $cleanupResponse.data.user.projectV2 }
if ($null -eq $cleanupProject) { Stop-ProjectCleanup 'Inventory Project was not found before deletion.'; return }
if ($cleanupProject.number -ne <project-number> -or $cleanupProject.title -ne '<escaped-project-title>' -or $cleanupProject.url -ne '<project-url>') {
    Stop-ProjectCleanup 'Live Project identity does not match the cleanup inventory.'
    return
}
$global:GHPMV_CLEANUP_PROJECT_ID = $cleanupProject.id
Write-Output ("GHPMV_CLEANUP_PROJECT_CONFIRMED:{0}" -f $cleanupProject.url)
$global:LASTEXITCODE = 0
```

2. **照合済み Project を削除する**

```powershell
function Stop-ProjectDelete([string]$Message) { Write-Error $Message; $global:LASTEXITCODE = 1 }
if ([string]::IsNullOrWhiteSpace($global:GHPMV_CLEANUP_PROJECT_ID)) { Stop-ProjectDelete 'No confirmed Project node ID is available.'; return }
$cleanupToken = [Environment]::GetEnvironmentVariable('<cleanup-token-env-var>')
if ([string]::IsNullOrWhiteSpace($cleanupToken)) { Stop-ProjectDelete 'Cleanup token is not available in the token execution terminal.'; return }
$cleanupHostArguments = if ('<cleanup-host>' -eq 'github.com') { @() } else { @('--hostname', '<cleanup-host>') }
$previousCleanupToken = $env:GH_TOKEN
try {
    $env:GH_TOKEN = $cleanupToken
    gh api @cleanupHostArguments graphql -f query='mutation($projectId:ID!){deleteProjectV2(input:{projectId:$projectId}){clientMutationId}}' -F projectId=$global:GHPMV_CLEANUP_PROJECT_ID
    if ($LASTEXITCODE -ne 0) { Stop-ProjectDelete 'Project deletion failed.'; return }
}
finally {
    if ($null -eq $previousCleanupToken) { Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue } else { $env:GH_TOKEN = $previousCleanupToken }
}
Write-Output 'GHPMV_CLEANUP_PROJECT_DELETED'
$global:LASTEXITCODE = 0
```

3. **同じ owner / number が存在しないことを read-back する**

```powershell
function Stop-ProjectReadBack([string]$Message) { Write-Error $Message; $global:LASTEXITCODE = 1 }
$cleanupToken = [Environment]::GetEnvironmentVariable('<cleanup-token-env-var>')
if ([string]::IsNullOrWhiteSpace($cleanupToken)) { Stop-ProjectReadBack 'Cleanup token is not available in the token execution terminal.'; return }
$cleanupHostArguments = if ('<cleanup-host>' -eq 'github.com') { @() } else { @('--hostname', '<cleanup-host>') }
$cleanupQuery = if ('<owner-type>' -eq 'organization') {
    'query($login:String!,$number:Int!){organization(login:$login){projectV2(number:$number){id}}}'
} else {
    'query($login:String!,$number:Int!){user(login:$login){projectV2(number:$number){id}}}'
}
$previousCleanupToken = $env:GH_TOKEN
try {
    $env:GH_TOKEN = $cleanupToken
    $cleanupReadBackOutput = gh api @cleanupHostArguments graphql -f query=$cleanupQuery -f login='<owner-login>' -F number=<project-number> 2>&1
    $cleanupReadBackExitCode = $LASTEXITCODE
}
finally {
    if ($null -eq $previousCleanupToken) { Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue } else { $env:GH_TOKEN = $previousCleanupToken }
}
$cleanupReadBackText = $cleanupReadBackOutput | Out-String
$expectedNotFound = $cleanupReadBackExitCode -ne 0 -and
    $cleanupReadBackText -match ('Could not resolve to a ProjectV2 with the number ' + [regex]::Escape('<project-number>'))
if (!$expectedNotFound) {
    if ($cleanupReadBackExitCode -ne 0) { Stop-ProjectReadBack 'Project cleanup read-back failed with an unexpected error.'; return }
    $cleanupReadBack = $cleanupReadBackText | ConvertFrom-Json
    $remainingProject = if ('<owner-type>' -eq 'organization') { $cleanupReadBack.data.organization.projectV2 } else { $cleanupReadBack.data.user.projectV2 }
    if ($null -ne $remainingProject) { Stop-ProjectReadBack 'Project still exists after deletion.'; return }
}
Remove-Variable GHPMV_CLEANUP_PROJECT_ID -Scope Global -ErrorAction SilentlyContinue
Write-Output 'GHPMV_CLEANUP_PROJECT_ABSENT'
$global:LASTEXITCODE = 0
```

### Repository cleanup commands

`created` repository がある場合は、最終 cleanup choices を表示する前に「repository も削除する意図があるか」を一問で確認する。削除しない回答なら repository entry を `retained` とし、Project だけの cleanup choices を表示する。削除する回答なら、次の credential を準備して permission preflight を通過してから、repository を含む最終 cleanup choices を表示する。

- fixture repository + classic PAT: 該当 side の token に `delete_repo` があること
- fixture repository + fine-grained PAT: 対象 repository が選択され、Administration: write、organization approval が Active であること
- GEI target repository + destination token owner が organization owner: destination classic PAT に `delete_repo` があること
- GEI target repository + destination token owner が Migrator: target organization owner の一時 classic PAT を `GHPMV_TARGET_CLEANUP_TOKEN` に準備し、`delete_repo` を付けること

現在の classic PAT に `delete_repo` がない場合や、fine-grained PAT に対象 repository の Administration: write がない場合も、source は `GHPMV_SOURCE_CLEANUP_TOKEN`、target は `GHPMV_TARGET_CLEANUP_TOKEN` という一時 credential を使う。該当 host の classic PAT 作成 URL、organization owner / repository admin role、`delete_repo` scope、SSO authorization を示し、Step 4 と同じ一-token secure-input 手順で次を送る。

```powershell
$cleanupSecureToken = Read-Host "<cleanup-env-var> for <owner-login> on <cleanup-host> (classic PAT with delete_repo for repository cleanup)" -AsSecureString; [Environment]::SetEnvironmentVariable('<cleanup-env-var>', [System.Net.NetworkCredential]::new("", $cleanupSecureToken).Password, 'Process'); if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('<cleanup-env-var>'))) { Remove-Item "Env:<cleanup-env-var>" -ErrorAction SilentlyContinue; Write-Output "GHPMV_REPOSITORY_CLEANUP_TOKEN_MISSING:<token-prompt-id>" } else { Write-Output "GHPMV_REPOSITORY_CLEANUP_TOKEN_READY:<token-prompt-id>" }
```

repository entry ごとに、削除用 token と permission を次の一 command で preflight する。`<cleanup-token-type>` は `classic` / `fine-grained`、fine-grained の `<administration-write-confirmed>` は作成 URL、repository selection、Active approval を確認した場合だけ `$true` に置き換える。

```powershell
function Stop-RepositoryCleanupPreflight([string]$Message) { Write-Error $Message; $global:LASTEXITCODE = 1 }
$cleanupToken = [Environment]::GetEnvironmentVariable('<cleanup-token-env-var>')
if ([string]::IsNullOrWhiteSpace($cleanupToken)) { Stop-RepositoryCleanupPreflight 'Cleanup token is not available in the token execution terminal.'; return }
$cleanupHostArguments = if ('<cleanup-host>' -eq 'github.com') { @() } else { @('--hostname', '<cleanup-host>') }
$cleanupAdministrationWriteConfirmed = <administration-write-confirmed>
$previousCleanupToken = $env:GH_TOKEN
try {
    $env:GH_TOKEN = $cleanupToken
    $cleanupPreflightResponse = gh api @cleanupHostArguments --include 'repos/<owner>/<repository>' 2>&1
    if ($LASTEXITCODE -ne 0) { Stop-RepositoryCleanupPreflight 'Repository cleanup permission preflight failed.'; return }
}
finally {
    if ($null -eq $previousCleanupToken) { Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue } else { $env:GH_TOKEN = $previousCleanupToken }
}
$cleanupPreflightText = $cleanupPreflightResponse | Out-String
$cleanupBodyMatch = [regex]::Match($cleanupPreflightText, '(?s)\{.*\}\s*$')
if (!$cleanupBodyMatch.Success) { Stop-RepositoryCleanupPreflight 'Repository cleanup preflight returned no JSON body.'; return }
$cleanupRepository = $cleanupBodyMatch.Value | ConvertFrom-Json
if ($cleanupRepository.full_name -ne '<owner>/<repository>' -or $cleanupRepository.permissions.admin -ne $true) {
    Stop-RepositoryCleanupPreflight 'Cleanup identity does not have effective repository admin access.'
    return
}
if ('<cleanup-token-type>' -eq 'classic') {
    $scopeMatch = [regex]::Match($cleanupPreflightText, '(?im)^x-oauth-scopes:\s*(.+)$')
    $scopes = if ($scopeMatch.Success) { @($scopeMatch.Groups[1].Value -split ',' | ForEach-Object Trim) } else { @() }
    if ('delete_repo' -notin $scopes) { Stop-RepositoryCleanupPreflight 'Classic cleanup PAT is missing delete_repo.'; return }
}
elseif (!$cleanupAdministrationWriteConfirmed) {
    Stop-RepositoryCleanupPreflight 'Fine-grained cleanup PAT Administration: write and Active approval were not confirmed.'
    return
}
Write-Output 'GHPMV_CLEANUP_REPOSITORY_PERMISSION_READY'
$global:LASTEXITCODE = 0
```

preflight が失敗したら repository 削除を選択肢に出さず `retained` とし、権限を広げるかどうかを別ターンで確認する。成功後、repository ごとに次の 3 command を別々に送る。`pre-existing` entry には送らない。

1. **full name / URL を inventory と照合する**

```powershell
function Stop-RepositoryCleanup([string]$Message) { Write-Error $Message; $global:LASTEXITCODE = 1 }
$cleanupToken = [Environment]::GetEnvironmentVariable('<cleanup-token-env-var>')
if ([string]::IsNullOrWhiteSpace($cleanupToken)) { Stop-RepositoryCleanup 'Cleanup token is not available in the token execution terminal.'; return }
$cleanupHostArguments = if ('<cleanup-host>' -eq 'github.com') { @() } else { @('--hostname', '<cleanup-host>') }
$previousCleanupToken = $env:GH_TOKEN
try {
    $env:GH_TOKEN = $cleanupToken
    $cleanupRepositoryText = gh api @cleanupHostArguments 'repos/<owner>/<repository>'
    if ($LASTEXITCODE -ne 0) { Stop-RepositoryCleanup 'Repository cleanup lookup failed.'; return }
}
finally {
    if ($null -eq $previousCleanupToken) { Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue } else { $env:GH_TOKEN = $previousCleanupToken }
}
$cleanupRepository = $cleanupRepositoryText | ConvertFrom-Json
if ($cleanupRepository.full_name -ne '<owner>/<repository>' -or $cleanupRepository.html_url -ne '<repository-url>') {
    Stop-RepositoryCleanup 'Live repository identity does not match the cleanup inventory.'
    return
}
Write-Output ("GHPMV_CLEANUP_REPOSITORY_CONFIRMED:{0}" -f $cleanupRepository.html_url)
$global:LASTEXITCODE = 0
```

2. **照合済み repository を削除する**

```powershell
function Stop-RepositoryDelete([string]$Message) { Write-Error $Message; $global:LASTEXITCODE = 1 }
$cleanupToken = [Environment]::GetEnvironmentVariable('<cleanup-token-env-var>')
if ([string]::IsNullOrWhiteSpace($cleanupToken)) { Stop-RepositoryDelete 'Cleanup token is not available in the token execution terminal.'; return }
$cleanupHostArguments = if ('<cleanup-host>' -eq 'github.com') { @() } else { @('--hostname', '<cleanup-host>') }
$previousCleanupToken = $env:GH_TOKEN
try {
    $env:GH_TOKEN = $cleanupToken
    gh api @cleanupHostArguments --method DELETE 'repos/<owner>/<repository>'
    if ($LASTEXITCODE -ne 0) { Stop-RepositoryDelete 'Repository deletion failed.'; return }
}
finally {
    if ($null -eq $previousCleanupToken) { Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue } else { $env:GH_TOKEN = $previousCleanupToken }
}
Write-Output 'GHPMV_CLEANUP_REPOSITORY_DELETED'
$global:LASTEXITCODE = 0
```

3. **HTTP 404 を read-back する**

```powershell
function Stop-RepositoryReadBack([string]$Message) { Write-Error $Message; $global:LASTEXITCODE = 1 }
$cleanupToken = [Environment]::GetEnvironmentVariable('<cleanup-token-env-var>')
if ([string]::IsNullOrWhiteSpace($cleanupToken)) { Stop-RepositoryReadBack 'Cleanup token is not available in the token execution terminal.'; return }
$cleanupHostArguments = if ('<cleanup-host>' -eq 'github.com') { @() } else { @('--hostname', '<cleanup-host>') }
$previousCleanupToken = $env:GH_TOKEN
try {
    $env:GH_TOKEN = $cleanupToken
    $cleanupRepositoryReadBack = gh api @cleanupHostArguments 'repos/<owner>/<repository>' 2>&1
    $cleanupRepositoryReadBackExitCode = $LASTEXITCODE
}
finally {
    if ($null -eq $previousCleanupToken) { Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue } else { $env:GH_TOKEN = $previousCleanupToken }
}
$cleanupRepositoryReadBackText = $cleanupRepositoryReadBack | Out-String
if ($cleanupRepositoryReadBackExitCode -eq 0 -or $cleanupRepositoryReadBackText -notmatch 'HTTP 404') {
    Stop-RepositoryReadBack 'Repository read-back did not confirm HTTP 404.'
    return
}
Write-Output 'GHPMV_CLEANUP_REPOSITORY_ABSENT'
$global:LASTEXITCODE = 0
```

## E2E settings の読み込み

Step 1の質問を始める前に、`GHPMV_E2E_SETTINGS`が設定されている場合は、そのpathだけをauthoritativeなJSONCとして読む。明示pathが存在しない、読み取れない、またはvalidationに失敗した場合は、local/shared fileへfallbackせず、pathとエラーを示して修正されるまで停止する。`GHPMV_E2E_SETTINGS`が未設定の場合だけ、`tests/e2e.settings.local.jsonc`、`tests/e2e.settings.jsonc`の順で最初に存在するfileを読む。`//`コメントと末尾commaを許可する。設定値は次の用途に使い、同じ非secret値を再質問しない。

自動検出ではglobやtracked-file一覧だけに依存しない。`tests/e2e.settings.local.jsonc`はgitignore対象のため、file search結果に現れないことがある。必ずrepository rootから`Test-Path -LiteralPath tests/e2e.settings.local.jsonc`でexact pathの存在を先に確認し、存在すればshared fileを読む前にlocal fileを読む。local fileが存在するのにshared fileを先に読んだり、localに非空で設定されたlogin、organization、repository、policy confirmationを再質問したりしてはならない。

- source / target Organization、API / Web / uploads URL、browser profile
- Integration / Browser fixtureのProject番号とsource / target repository
- source / target browser login、collaborator login、EMUを含むuser mapping
- fixture preparation、GEIまたはfixture-seedのrepository preparation mode
- GEI source / target repository、visibility、token owner login、role status
- Repository migrations ruleset bypass status
- source / target account の Organization administrator 確認
- source / target の Projects 有効化と private repository 作成 policy 確認
- source / target の ghpmv PAT type (`classic` / `fine-grained`)
- PATおよびbrowser stateを保持する**環境変数名**

自動検出したlocal/shared fileでは、空文字、存在しないlocal resource、現在のhostと矛盾するURL、`classic` / `fine-grained` 以外の `tokenType`、またはschema validationに失敗する値を確定値として扱わず、その項目だけを通常どおり質問する。明示指定した`GHPMV_E2E_SETTINGS`のエラーだけはfallbackや質問による補完をせず停止する。JSONCにはPAT値、cookie、browser storage-state内容を保存させない。`tokenEnvironmentVariable`などの値は環境変数名であり、secretそのものではない。

`browser-e2e`で`users.sourceBrowserLogin`と`users.targetBrowserLogin`が両方とも非空なら、source / target browser accountが同一か別かを質問しない。account identityは正規化した`webBaseUrl` hostとbrowser loginの組で機械判定する。同じhostでloginがcase-insensitiveに一致する場合だけ同一account、hostまたはloginが異なる場合は別accountとして記録する。どちらかのloginが空の場合だけ不足しているloginを一件ずつ質問し、両方確定後に同じ規則で判定する。

`source.apiBaseUrl` / `target.apiBaseUrl`はghpmv用GraphQL endpointで、`https://api.TENANT.ghe.com/graphql`またはtenant API originのどちらも受け付ける。GEIの`--github-source-api-url` / `--target-api-url`へ渡す値は別に導出し、末尾のoptional `/graphql`とtrailing slashを除いた`https://api.TENANT.ghe.com` originを使う。GraphQL endpointをそのままGEI argumentへ再利用しない。

settings由来のOrganization loginは`^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$`、repository short nameは`^(?!\.{1,2}$)[A-Za-z0-9._-]{1,100}$`、user loginは`^[A-Za-z0-9](?:[A-Za-z0-9_-]{0,98}[A-Za-z0-9])?$`で検証する。これらを含むsettings由来の文字列をPowerShell commandへ渡す場合は、profileだけでなくOrganization、repository、login、URL、title、path、mapping値をすべてsingle-quoted argumentにする。値のpatternがsingle quoteを許可する項目では、既存の規則どおり`'`を`''`へ置換してから囲む。検証済みであってもunquoted substitutionは行わない。

このSkill内の`SOURCE_TOKEN`と`TARGET_TOKEN`は役割を示す既定名である。settingsを読み込んだ場合は、以後のrequired token inventory、readiness check、PAT入力prompt、preflight、fixture、export、import、verifyの全commandで、それぞれ`source.tokenEnvironmentVariable`と`target.tokenEnvironmentVariable`の実値へ置き換える。GEIも同様に`gei.sourceTokenEnvironmentVariable`と`gei.targetTokenEnvironmentVariable`を使う。設定した変数を別の固定名として再入力させたり、固定名だけを確認してmissingと判定したりしない。sentinelの表示名はsecretを含まないため従来の`GHPMV_SOURCE_TOKEN_READY`などを維持してよい。

settings の `source.tokenType` / `target.tokenType` が `classic` または `fine-grained` なら、対応する ghpmv token type として記録し、同じ PAT type を質問しない。`null` または省略時だけ、選択経路で必要になった side を一件ずつ質問する。GEI source / destination credential は引き続き classic 固定であり、endpoint の `tokenType` を GEI credentialへ流用しない。

同様に、command例にあるliteral `source` / `target` browser profileは既定値である。settingsを読み込んだ場合、`login`、fixture UI、export、import、verifyのすべての`--profile` / `--browser-profile`を、それぞれ`source.browserProfile` / `target.browserProfile`へ置き換える。profile名は`^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`に一致し、sourceとtargetで異なることを使用前に検証する。生成するPowerShell commandでは、検証済みprofileも必ずsingle-quoted argument（例: `--profile 'source'`）として渡す。profile名が設定済みなのに固定名のstorage-stateを使ったり、unquotedでcommandへ展開したりしてはならない。

settings の `execution.fixturePreparation`、`execution.repositoryPreparationMode`、`gei.sourceRole`、`gei.targetRole`、`gei.repositoryMigrationsBypass`、endpoint の `tokenType`、`organizationAdministrator`、`projectsEnabled`、`privateRepositoryCreationAllowed` は明示的な非secret確認値として採用し、値が有効なら同じ内容を対話用質問で再確認しない。`false`、`null`、`unconfirmed` が選択経路の必須条件を満たさない場合は不足を示して停止し、確認質問で補完しない。実resource作成前の影響説明、選択済み PAT type に必要な permission / Active approval、warning許容、cleanup同意は省略しない。`migrator-pending`は`migrator-active`として扱わず、`createTemporaryTargetProject`は削除同意を意味しない。

## Step 1: 確認範囲を決める

次から一つ選んでもらう。質問文にも「実 resource への影響」を判断基準として示す。choice は次のように、内部 ID だけでなく実行範囲を表示する。

1. `build-only — restore + build のみ（GitHub 実 resource へのアクセスなし）`
2. `baseline-full — build + deterministic tests + CLI smoke（GitHub 実 resource へのアクセスなし）`
3. `read-only — 実 Project を export のみ（source を読み取り、target への作成・変更なし）`
4. `api-only — API で export → import → verify（fixture / target resource を作成・変更）`
5. `browser-e2e — browser automation、fixture、export → import → verify の完全 E2E（Recommended、source / target resource を作成・変更）`

依頼が browser automation を含む実環境 E2E 検証である場合は 5 に `(Recommended)` を表示する。別目的で起動された場合は、その依頼に最も直接対応する choice だけを推奨する。質問文または choice 内で、`api-only` と `browser-e2e` は実 resource を作成・変更するが、この時点ではまだ実行せず、後続 Step で作成物と削除同意を確認することを示す。

1 は `build-only`、2 は `baseline-full` として記録する。選択結果を `validation mode` として記録し、次の経路以外へ進めない。

| validation mode | 実行する Step | 終了条件 |
|---|---|---|
| `build-only` | 2 | restore + build 成功後に終了する。test、CLI smoke、token、browser、fixture、実環境操作を案内しない。 |
| `baseline-full` | 2 | build + deterministic tests + CLI smoke 完了後に終了する。token、browser、fixture、実環境操作を案内しない。 |
| `read-only` | 2, 4, 6 | Step 2 は restore + build だけ実行する。source token だけを準備し、Step 6 の browser option なしの export 完了後に終了する。Step 3, 5, 7-10 は実行しない。 |
| `api-only` | 2, 4, 必要な場合だけ 5, 6-10 | Step 2 は restore + build だけ実行する。browser profile を準備せず、browser option をすべて外して実行する。 |
| `browser-e2e` | 2-10 | Step 2 は restore + build だけ実行する。Step 5 は source fixture の作成または既存 fixture contract の確認として必ず通り、browser profile、field-sum coverage、fixture / GEI / browser enrichment を含む full flow を実行する。 |

`api-only` または `browser-e2e` では、settings の `execution.fixturePreparation` を `fixture preparation` として記録し、設定済みなら質問しない。設定がない場合だけ、既存 source Project を使うか fixture を作るかを一問で確認する。`api-only` の `existing` は Step 5 を実行せず、fixture 作成用権限を要求しない。`browser-e2e` の `existing` は resource を作成しない確認 Step として Step 5 を通り、現行標準 fixture contract を記録する。

`browser-e2e` の fixture preparation 質問では、既存 round-trip が grouped Table / Roadmap の Field sum も検証することを質問文に含める。`create` は現行の標準 fixture が required Number fields と 4 Views を決定的に作るため推奨する。`existing` は arbitrary Project ではなく、下記 contract を満たす現行標準 fixture または同等構成に限る。Step 6 の snapshot gate が不一致なら手編集で続行せず、新しい標準 fixture を作るか明示的に選び直す。

同じ mode では、settings の `execution.repositoryPreparationMode` を `repository preparation mode` として記録し、設定済みなら質問しない。設定がない場合だけ、target repository を GEI で移行するか fixture seed で作るかを Step 4 より前に一問で確認する。token の用途が決まるまで PAT の入力を求めない。

GEI は GitHub.com source と GHEC with data residency source の両方を扱う。data-residency sourceでは`gh gei migrate-repo --github-source-api-url <source-api-url>`、data-residency targetでは`--target-api-url <target-api-url> --target-uploads-url <target-uploads-url>`を使う。source / target endpointを取り違えたり、source hostをGitHub.comとして偽って続行してはならない。

`GEI` を選んだ場合は、settings の `gei.sourceRole` と `gei.targetRole` を `GEI source / destination role status` として記録し、設定済みなら質問しない。設定がない側だけ、token owner の現在または予定している organization role を次の三択で確認する。

1. Organization owner
2. Migrator（適用済み）
3. Migrator にする予定（まだ適用していない）

3 を選んだ場合は 2 として扱わない。必要なロール設定を済ませるよう案内し、適用済みと確認できるまで GEI token の入力、migration command、Step 7 へ進まない。適用後に改めて role status を確認し、`migrator-active` へ更新する。

GEI roleは`GHPMV_GEI_SOURCE_TOKEN` / `GHPMV_GEI_TARGET_TOKEN`にだけ適用する。標準fixtureはsourceでorganization Issue Fieldを作成し、target importでも同じIssue Fieldを作成するため、GitHubの[Create issue field for an organization](https://docs.github.com/en/rest/orgs/issue-fields#create-issue-field-for-an-organization)仕様上、対応する`SOURCE_TOKEN` / `TARGET_TOKEN`のauthenticated userは各organizationのadministratorでなければならない。Migrator roleやclassic PATの`admin:org` scopeだけではadministrator roleを代替できない。

`fixture preparation=create`ではsource endpoint の `organizationAdministrator=true` を、`api-only` / `browser-e2e`ではtarget endpoint の `organizationAdministrator=true` を必須とする。settings で true なら質問せず記録する。false または null ならPAT作成・入力、Browser login、permission preflightへ進まず、administrator accountへ切り替えるかを一問で確認する。切り替える場合はlogin、token owner、browser profile、user mapping用target loginを同じaccountへ更新する。standard fixtureを使わず、snapshotにもorganization Issue Fieldがない既存Project経路だけはこのadministrator gateを要求しない。

標準fixture経路では、source endpoint の `privateRepositoryCreationAllowed=true` と `projectsEnabled=true`、target endpoint の `projectsEnabled=true` を必須とする。settings で true なら質問しない。false または null なら不足項目だけを一問で確認し、未確認のままPAT入力へ進まない。既存Project経路ではsource resourceを作成しないためrepository creation policyを要求せず、export後のsnapshotに応じてtarget側のIssue Field、collaborator、visibility、linked repository権限だけを要求する。

`read-only`、`api-only`、`browser-e2e` では、host / account 値を次の順で一問ずつ確認する。

1. source host type: **GitHub.com（通常の GHEC を含む）** または **GHEC with data residency (`*.ghe.com`)**
2. `api-only` / `browser-e2e` では target host type も同じ二択で確認する。
3. data residency を選んだ側ごとに、placeholder ではない tenant web URL (`https://TENANT.ghe.com`) を自由入力の質問カードで確認する。対応する API URL (`https://api.TENANT.ghe.com`) を導出して別の確認カードで提示し、確定する。target側ではuploads URL (`https://uploads.TENANT.ghe.com`) も導出して別の確認カードで確定する。
4. `browser-e2e` では、settingsのbrowser loginが片側でも空の場合だけ不足loginを確認する。両login確定後は`(web host, login)`で同一/別accountを機械判定し、同一か別かを別質問で確認しない。

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

`repository preparation mode` が `GEI` の場合は、実 resource 作成や PAT 入力より前に、同じ terminal で次を一 command ずつ実行する。

```powershell
gh --version
gh gei migrate-repo --help
```

`gh gei migrate-repo --help` が extension 未インストールを理由に失敗した場合だけ、次を一 command ずつ実行し、今回の sentinel と exit code 0 を確認する。

```powershell
gh extension install github/gh-gei
gh gei migrate-repo --help
```

通常は既存 extension を自動 upgrade しない。help に `--github-source-org`、`--source-repo`、`--github-target-org`、`--target-repo`、`--target-repo-visibility` があることをagentが出力から確認する。data-residency sourceでは`--github-source-api-url`、data-residency targetでは`--target-api-url`と`--target-uploads-url`も必須とする。

選択topologyに必要なoptionだけがhelpにない場合は、実resource作成やPAT入力より前に次を一度実行し、再度helpを確認する。

```powershell
gh extension upgrade github/gh-gei
```

install / upgrade失敗、help失敗、upgrade後も必須option不足のいずれかがあれば、Browser login、PAT入力、fixture作成へ進まず停止する。

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

各 `login` command は agent が起動し、browser sign-in 中も command の終了を監視する。ユーザーへは現在開いている browser で期待 login として sign in することだけを案内し、ログイン完了の返信を求めない。この Step では PAT や token 入力に言及しない。`Signed in as '<reported-login>'` から login を取り出し、期待 login と大文字小文字を区別せず一致すること、および exit code 0 を確認してから次の profile へ進み、保存先の browser state path を記録する。

## Step 4: Token を準備する

**PAT の入力を求める前に、経路から exact `required token inventory` を作成する。** inventory には env var、host、organization、token owner、用途、token type、role、scope / permission、作成 URL status、secure input 順を含める。次の env var を一件も省略しない。

| 経路 | required token inventory |
|---|---|
| `read-only` | `SOURCE_TOKEN` |
| `api-only` / `browser-e2e` + `fixture-seed` | `SOURCE_TOKEN`, `TARGET_TOKEN` |
| `api-only` / `browser-e2e` + `GEI` | `SOURCE_TOKEN`, `TARGET_TOKEN`, `GHPMV_GEI_SOURCE_TOKEN`, `GHPMV_GEI_TARGET_TOKEN` |

`SOURCE_TOKEN` / `TARGET_TOKEN` は ghpmv 用である。settings の対応する `tokenType` が有効ならその値を採用して質問せず、未設定の side だけユーザーに token type を一つずつ選んでもらう。GEI 用の二件は classic PAT credential 固定であり、token type の質問をしない。GEI では source と destination の両方の classic PAT credential が必須である。別 token 値の発行は推奨だが、既存の classic PAT を再利用してもよい。再利用する場合も必要 scope の和集合、SSO authorization、organization role を満たし、workflow 上は二つの `GHPMV_GEI_*` env var を必ず ready にする。fine-grained PAT を GEI 用に再利用してはならない。

secure input 順は `SOURCE_TOKEN`、`TARGET_TOKEN`、GEI 経路の場合は `GHPMV_GEI_SOURCE_TOKEN`、`GHPMV_GEI_TARGET_TOKEN` とする。各 readiness sentinel を確認してから次の一件を送る。

`fixture preparation=create`では標準fixtureのcapabilityが既知なので、上記全inventoryを一つのphaseで準備する。`fixture preparation=existing`ではsnapshot内容がexportまで未確定のため、最初のphaseでは`SOURCE_TOKEN`だけを作成・入力する。Step 6の`requirements`結果を確認した後、`TARGET_TOKEN`とGEI tokenのtype / role / permission / URLを確定し、残りのsecure inputを同じterminalで行う。snapshot未確認のままtargetへ過剰なroleやpermissionを要求しない。

**PAT の入力を求める前に、現在のphaseで選択済みの token type に必要な権限を提示する。** token type が未設定で質問が必要な場合は、選択前に classic / fine-grained の差を示す。現在phaseの token type を state に記録し終えるまで URL の生成、readiness 質問、`Read-Host` のいずれにも進まない。settings だけで必要な token type がすべて確定した場合は PAT type 質問を挟まず、Step 3 完了後の同じ turn で token plan と作成 URL を表示して readiness question へ進む。最後の token type 回答で URL 生成に必要な値がすべて揃った場合も、その同じ turn の次の assistant 本文は必ず token plan と作成 URL を含める。「準備します」「次に URL を出します」という遷移文だけで停止したり、別の質問や terminal command を挟んだりしてはならない。

token plan は `env var | host / organization | 用途 | type | role | scope / permission | creation URL` の表で表示する。標準fixtureの`SOURCE_TOKEN` / `TARGET_TOKEN`のroleは`organization administrator`、GEI tokenのroleは別途確認したownerまたはmigrator statusを表示する。fine-grained を選んだ side には pre-filled URL、classic を選んだ side と二つの GEI token には host に対応する classic PAT 作成ページ URL と scope を表示する。作成 URL を表示した同じ response で、現在phaseの全PATの準備状況を一つの readiness question で確認してから `Read-Host` へ進む。

fine-grained PAT を選んだ token は URL status を `pending` にする。source / target organization login、host、fixture preparation、repository preparation mode が未確定なら、先に不足値を質問する。該当 token の完全な pre-filled URL を assistant 本文へ表示して検証し、status を `shown-and-validated` に更新するまで、次の操作を禁止する。

- 「必要な権限を準備できましたか」という質問
- `Read-Host` による PAT 入力
- preflight、fixture、export、GEI、import、verify

source と target の両方が fine-grained の場合は、**Source fine-grained PAT** と **Target fine-grained PAT** の見出しを付け、同じ assistant 本文に両方の clickable URL を表示する。permission の文章だけを列挙して URL を省略してはならない。URL を `ask_user.question` や `choices` に埋め込まない。

agent が対話 terminal を操作できる場合は、`Read-Host` command を同じ terminal instance の `send_terminal_input` actionへ agent が送信し、ユーザーには表示された prompt へ PAT 値だけを入力してもらう。readiness command を送信して出力を読めた terminal は操作可能であるため、shell tool を試したり、`Read-Host` command を本文へ掲載してユーザーに実行させたりしない。agent が terminal canvas を開くことも入力 action を呼ぶこともできない場合に限り、`Read-Host` command を質問カードより前の assistant 本文へ code block として掲載する。入力完了後は Step 4 の preflight から Step 10 まで、token を設定した同じ PowerShell terminal で command を実行する。agent の shell tool が別 process で動く場合は、token を必要とする command をその tool へ切り替えない。

mode と repository preparation mode から作成した `required token inventory` に存在する token だけを準備する。source resourceを読むStep 5/6へ進むには`SOURCE_TOKEN`がreadyでなければならない。GEIへ進む前には`TARGET_TOKEN`と二件のGEI tokenを含む四件すべて、fixture-seed / import / verifyへ進む前には`SOURCE_TOKEN`と`TARGET_TOKEN`がreadyでなければならない。

`setup --fixture` で organization repository を自動作成する完全自動経路では、確実性を優先する場合は classic PAT を推奨する。fine-grained PAT を選んだ場合は、下記の permission 設定だけで成功とみなさず、fixture 実行前に repository を作成しない preflight を必ず行う。

### Fine-grained PAT 作成 URL

ユーザーが fine-grained PAT を選んだ場合は、permission を手作業で列挙させるだけでなく、GitHub の [pre-filled fine-grained PAT URL](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#pre-filling-fine-grained-personal-access-token-details-using-url-parameters) を現在の経路に合わせて生成し、クリック可能な完全な URL として提示する。GitHub.com 側は `https://github.com/settings/personal-access-tokens/new`、data residency 側は `https://TENANT.ghe.com/settings/personal-access-tokens/new` を使う。`target_name` には確認済みの organization login を設定し、`name`、`description`、`expires_in=30` と次の permission query parameter を付ける。

現在の経路で必要な全 token type を確定し、そのうち一つ以上で fine-grained を選択した直後は、次の state machine を厳守する。

1. 新しい質問を出さず、確認済みの token type / host / organization / fixture 経路から、fine-grained を選んだ side の URL を内部で組み立てる。classic を選んだ side の fine-grained URL は生成しない。
2. 次の assistant 本文に token plan と、fine-grained を選んだ token ごとの label、placeholder のない完全な raw autolink を実際に表示する。classic / GEI token の作成ページ URL も同じ本文に表示する。「これから生成します」「後で表示します」という予告だけで終わらせない。
3. URL を含むその同じ assistant response で、現在phaseの全PATを対象に readiness 用 `ask_user` を一度だけ呼ぶ。
4. choices は `このphaseに必要なPATをすべて作成・承認済み` と `まだ準備中` にする。一部 token だけを準備済みとして secure input へ進まない。

assistant response 本文に今回の完全な URL が一つも存在しない状態では、`PAT を準備できましたか？`、permission 確認、PAT terminal 入力のいずれにも進んではならない。URL を生成できない必須値がある場合だけ、その不足値を一つ質問する。

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

URL 内に literal `\n`、escaped newline、空白、Markdown link label を混ぜない。renderer 上で折り返されても href 自体は一つの URL になるようにする。URL を表示した assistant 本文の直後に `ask_user` を呼ぶ場合、質問カードには readiness の確認文と choices だけを渡し、URL や Markdown を重複させない。「URL を生成して確認します」という本文の後に URL なしで readiness card を表示することは禁止する。

PAT URL を表示した turn は、URL の表示だけで終了してはならない。同じ turn で直ちに `ask_user` を呼び、次を一つの readiness question として明示する。

- 表示した fine-grained PAT URL を開いて PAT を作成する。
- 経路に必要な Repository access を選び、organization approval が必要なら **Active** まで待つ。
- token 値は会話へ貼らない。
- classic PAT は表示した host の作成ページで指定 scope を選び、必要なら SSO authorize する。
- GEI 経路では source / destination の classic PAT credential を両方準備する。
- required inventory の全 PAT が準備できたら、agent が右側 terminal に PAT 入力欄を一件ずつ自動表示する。ユーザーは表示された入力欄へ PAT 値だけを入力して Enter を押す。入力中の文字は画面に表示されない。

URL を表示した assistant response に `ask_user` tool call がない状態は workflow failure とする。「必要な PAT をすべて作成・承認済みかを確認します」という予告文だけで turn を終了してはならない。

`必要な PAT をすべて作成・承認済み` の回答を受けたら、遷移説明だけを返さず、同じ turn で最初の `Read-Host` command を `token execution terminal` の `send_terminal_input` actionへ送る。terminal 出力から PAT 入力待ちを確認した後、次の形式の入力完了質問を一枚だけ表示する。

> 右側のターミナルに `<ENV_VAR> for <organization> on <host> (<purpose>)` という PAT 入力欄を表示しました。そこへ該当する PAT 値だけを入力して Enter を押してください。入力中の文字は画面に表示されません。

choice は `<ENV_VAR> の入力を完了` とする。command、sentinel、marker、`Read-Host`、`hidden prompt` という内部用語を質問カードへ表示しない。`まだ準備中` または Cancel / Skipped の場合は pause し、URL を再生成したり PAT 入力へ進んだりしない。

作成 URL では **Repository access** を指定できない。URL を開いた後、現在の経路に応じて参照される全 repository または fixture 用の **All repositories** をユーザー自身に選んでもらい、permission と expiration を確認してから生成する。organization approval が必要なら **Active** になるまで待つ。data residency token を GitHub.com の settings URL で作らせたり、GitHub.com token を tenant API に使わせたりしない。classic PAT と GEI token にはこの URL を使わず、scope と SSO authorization を従来どおり案内する。

GEI でこれから新規作成する target repository は `TARGET_TOKEN` 作成時点では個別選択できない。この完全E2E経路でtargetにfine-grained PATを使う場合は、target organizationの **All repositories** を必須とする。All repositoriesを許可できない場合は、PAT入力前にtargetのtoken typeをclassicへ切り替え、必要scopeを再提示する。migration後のtoken設定変更を前提にしたままGEIへ進まない。

### Classic PAT

| token / 経路 | 必要な scope |
|---|---|
| source: 既存 Project の export | `read:project`。private repository の item / linked repository を読む場合は `repo` も追加。 |
| source: `setup --fixture` + export | `repo`, `project`, `admin:org`。この run が作る fixture repository を cleanup する場合は `delete_repo` も準備する。fixture が organization Issue Field を作成するため `admin:org` が必要。 |
| target: 既存または GEI 後 repository への import / verify | `project`, `read:org`。snapshot に organization Issue Field がある場合は `admin:org`、private target repository の item / linked repository 解決または Issue Field 値の書き込みには `repo` も追加。 |
| target: fixture seed + import / verify | `repo`, `project`, `admin:org`。この run が作る fixture repository を cleanup する場合は `delete_repo` も準備する。 |

Organization が要求する場合は classic PAT を SSO authorize する。

classic PAT には fine-grained PAT の permission pre-fill URL を使わない。token plan では host に対応する作成ページを完全な raw URL で表示し、表の scope をユーザーが選択する。

- GitHub.com: `https://github.com/settings/tokens/new`
- GHEC with data residency: `https://TENANT.ghe.com/settings/tokens/new`

data residency URL の `TENANT` は確認済みの実 subdomain に置き換える。placeholder のまま表示しない。

### Fine-grained PAT

fine-grained PAT は organization-owned Project にだけ使用する。GitHub は user-owned Project へのアクセスを current limitation としているため、`--owner-type user` では classic PAT を選ぶ。

| token / 経路 | Resource owner / repository access | 必要な permission |
|---|---|---|
| source: 既存 Project の export | source Project の owner。参照される全 repository を選択。 | Organization **Projects: Read-only**。organization Issue Field がある場合は Organization **Issue Fields: Read-only**。Repository **Metadata: Read-only**。private repository item には **Issues: Read-only** と **Pull requests: Read-only**。 |
| source: `setup --fixture` + export | source organization。**All repositories**。 | Repository **Administration: Read and write**、**Contents: Read and write**、**Issues: Read and write**、**Pull requests: Read and write**。Organization **Projects: Read and write**、**Issue Fields: Read and write**。 |
| target: 既存または GEI 後 repository への import / verify | target Project の owner。既存 repository は mapping / Workflow が参照する全 repository を選択。GEI で今から作る repository は **All repositories** 必須。許可できなければclassicを選ぶ。 | Organization **Projects: Read and write**。snapshot に organization Issue Field がある場合は Organization **Issue Fields: Read and write** と Repository **Issues: Read and write**。Repository **Metadata: Read-only**、linked repository には **Contents: Read and write**、private repository item には **Issues: Read-only** と **Pull requests: Read-only**。team collaborator を import する場合は Organization **Members: Read-only**。 |
| target: fixture seed + import / verify | target organization。**All repositories**。 | Repository **Administration: Read and write**、**Contents: Read and write**、**Issues: Read and write**、**Pull requests: Read and write**。Organization **Projects: Read and write**、**Issue Fields: Read and write**。team collaborator を import する場合は Organization **Members: Read-only**。 |

Organization が fine-grained PAT approval を要求する場合は承認済みであることを確認する。**既存 Project の export / import / verify だけを行うユーザーに fixture 作成用 permission を要求してはならない。**

GitHub の [fine-grained PAT permission matrix](https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens#organization-permissions-for-issue-fields) は、organization Issue Field の読み取りに **Issue Fields: read**、作成・更新・削除に **Issue Fields: write** を要求している。pre-filled URL の permission parameter は `issue_fields`。GitHub は Projects GraphQL mutation ごとの fine-grained PAT permission を公開していない。`linkProjectV2ToRepository` に対する Repository **Contents: Read and write** は実環境で確認した要件として案内し、PAT 向け公式要件として断定しない。

### GEI 専用 token

`repository preparation mode` が `GEI` の場合、source と destination の classic PAT credential は必須である。workflow では `GHPMV_GEI_SOURCE_TOKEN` と `GHPMV_GEI_TARGET_TOKEN` を必須 env var として準備し、Step 7 でそれぞれ GEI が要求する `GH_SOURCE_PAT` と `GH_PAT` へ一時的に mapping する。別 token 値を `SOURCE_TOKEN` / `TARGET_TOKEN` から分離して発行することは推奨だが、GEI credential 自体を省略してよいという意味ではない。

source / destination の role status が `migrator-pending` の間は、次の scope を説明してもよいが PAT の入力は求めない。Migrator ロールが適用されたことを確認して `migrator-active` に更新してから進める。

| GEI token | token owner の role | 必要な classic PAT scope |
|---|---|---|
| source | Organization owner または source organization の migrator | `admin:org`, `repo` |
| destination | Organization owner | `repo`, `admin:org`, `workflow`。migrated repository を cleanup する場合は `delete_repo` も追加。 |
| destination | destination organization の migrator | `repo`, `read:org`, `workflow`。Migrator role だけでは repository 削除権限を保証しないため、cleanup には target organization owner の別 credential を使う。 |

同じ classic PAT を `ghpmv` と GEI で再利用する場合は、該当する scope の和集合が必要になる。不要な `admin:org` を `ghpmv` 専用 token に追加させない。

role status が `owner` または `migrator-active` になった後、secure input より前の token plan に次の作成ページを表示する。source は GitHub.com 固定である。destination が data residency の場合だけ確認済み tenant host を使う。

- GEI source on GitHub.com: `https://github.com/settings/tokens/new`
- GEI source with data residency: `https://TENANT.ghe.com/settings/tokens/new`
- GEI destination on GitHub.com: `https://github.com/settings/tokens/new`
- GEI destination with data residency: `https://TENANT.ghe.com/settings/tokens/new`

各data-residency URLの`TENANT`は確認済みの実subdomainへ置き換える。各 URL の直前または token plan の同じ行に、該当 role に対応する scope、SSO authorization、organization access が必要であることを示す。GEI source / destination tokenはStep 7より前に両方をreadyにし、どちらかを後回しにしたままmigrationへ進まない。

`read-only`:

```powershell
$sourceSecureToken = Read-Host "SOURCE_TOKEN for <source-org> on <source-host> (ghpmv read-only export)" -AsSecureString; $env:SOURCE_TOKEN = [System.Net.NetworkCredential]::new("", $sourceSecureToken).Password; if ([string]::IsNullOrWhiteSpace($env:SOURCE_TOKEN)) { Remove-Item Env:SOURCE_TOKEN -ErrorAction SilentlyContinue; Write-Output "GHPMV_SOURCE_TOKEN_MISSING:<token-prompt-id>" } else { Write-Output "GHPMV_SOURCE_TOKEN_READY:<token-prompt-id>" }
```

`api-only` と `browser-e2e`:

`fixture preparation=create` では source purpose を `ghpmv fixture creation/export`、`fixture preparation=existing` では `ghpmv export only` とする。placeholder の `<source-purpose>` を選択済み経路の実値へ置き換える。

```powershell
$sourceSecureToken = Read-Host "SOURCE_TOKEN for <source-org> on <source-host> (<source-purpose>)" -AsSecureString; $env:SOURCE_TOKEN = [System.Net.NetworkCredential]::new("", $sourceSecureToken).Password; if ([string]::IsNullOrWhiteSpace($env:SOURCE_TOKEN)) { Remove-Item Env:SOURCE_TOKEN -ErrorAction SilentlyContinue; Write-Output "GHPMV_SOURCE_TOKEN_MISSING:<token-prompt-id>" } else { Write-Output "GHPMV_SOURCE_TOKEN_READY:<token-prompt-id>" }
```

現在の `GHPMV_SOURCE_TOKEN_READY:<token-prompt-id>` を確認した後だけ、別の terminal input として target を送信する。

`repository preparation mode=fixture-seed` では target purpose を `ghpmv fixture seed/import/verify`、`GEI` では `ghpmv import/verify after GEI` とする。placeholder の `<target-purpose>` を選択済み経路の実値へ置き換える。

```powershell
$targetSecureToken = Read-Host "TARGET_TOKEN for <target-org> on <target-host> (<target-purpose>)" -AsSecureString; $env:TARGET_TOKEN = [System.Net.NetworkCredential]::new("", $targetSecureToken).Password; if ([string]::IsNullOrWhiteSpace($env:TARGET_TOKEN)) { Remove-Item Env:TARGET_TOKEN -ErrorAction SilentlyContinue; Write-Output "GHPMV_TARGET_TOKEN_MISSING:<token-prompt-id>" } else { Write-Output "GHPMV_TARGET_TOKEN_READY:<token-prompt-id>" }
```

`GEI`:

```powershell
$geiSourceSecureToken = Read-Host "GHPMV_GEI_SOURCE_TOKEN for <source-org> on <source-host> (classic PAT for GEI source)" -AsSecureString; $env:GHPMV_GEI_SOURCE_TOKEN = [System.Net.NetworkCredential]::new("", $geiSourceSecureToken).Password; if ([string]::IsNullOrWhiteSpace($env:GHPMV_GEI_SOURCE_TOKEN)) { Remove-Item Env:GHPMV_GEI_SOURCE_TOKEN -ErrorAction SilentlyContinue; Write-Output "GHPMV_GEI_SOURCE_TOKEN_MISSING:<token-prompt-id>" } else { Write-Output "GHPMV_GEI_SOURCE_TOKEN_READY:<token-prompt-id>" }
```

現在の `GHPMV_GEI_SOURCE_TOKEN_READY:<token-prompt-id>` を確認した後だけ、別の terminal input として destination を送信する。

```powershell
$geiTargetSecureToken = Read-Host "GHPMV_GEI_TARGET_TOKEN for <target-org> on <target-host> (classic PAT for GEI destination)" -AsSecureString; $env:GHPMV_GEI_TARGET_TOKEN = [System.Net.NetworkCredential]::new("", $geiTargetSecureToken).Password; if ([string]::IsNullOrWhiteSpace($env:GHPMV_GEI_TARGET_TOKEN)) { Remove-Item Env:GHPMV_GEI_TARGET_TOKEN -ErrorAction SilentlyContinue; Write-Output "GHPMV_GEI_TARGET_TOKEN_MISSING:<token-prompt-id>" } else { Write-Output "GHPMV_GEI_TARGET_TOKEN_READY:<token-prompt-id>" }
```

Step 5 以降の `ghpmv` native command では PAT を `--token` argument に展開しない。command ごとに、対応する `SOURCE_TOKEN` または `TARGET_TOKEN` を process-scoped `GHPMV_TOKEN` へ一時 mapping し、`GHPMV_TOKEN` より先に解決される既存の process-scoped `GITHUB_TOKEN` を一時削除して、`--token` を省略する。`finally` で両方の以前の値へ戻す。以前の値が `null` なら `Remove-Item Env:` で一時変数を削除する。これにより意図した side の PAT を確実に使いながら、PAT を process argument inspection へ露出させない。

### Fine-grained fixture token の preflight

`fixture preparation` が `create` で source に fine-grained PAT を選んだ場合、`setup --fixture` より先に次の preflight 専用 wrapper を endpoint ごとに別々に実行する。送信直前に一意な `<preflight-id>` を生成する。target の `fixture-seed` でも organization と token を置き換えて同じ確認を行う。

`repository preparation mode` が `GEI` でtargetにfine-grained PATを選んだ場合も、GEIやsource fixture作成より前にtarget organizationの`issue-fields` preflightだけを`TARGET_TOKEN`で実行する。`repos` preflightはrepository作成permissionを確認するもので、GEIが別のclassic PATでrepositoryを作るこの経路には要求しない。GEI後のtarget repository accessはStep 7のIssue / PR queryで確認する。

```powershell
$previousPreflightToken = $env:GH_TOKEN
$env:GH_TOKEN = $env:SOURCE_TOKEN
try {
    $preflightEndpoint = "<repos-or-issue-fields>"
    $preflightRequiredPermission = if ($preflightEndpoint -eq "repos") { "administration=write" } elseif ($preflightEndpoint -eq "issue-fields") { "issue_fields=write" } else { throw "Unsupported preflight endpoint: $preflightEndpoint" }
    $preflightResponse = '{}' | gh api --include --method POST --input - -H "X-GitHub-Api-Version: 2026-03-10" "orgs/<source-org>/$preflightEndpoint" 2>&1
    $preflightNativeExitCode = $LASTEXITCODE
    $preflightText = $preflightResponse | Out-String
    Write-Output $preflightResponse
    $preflightPermissionPattern = 'X-Accepted-GitHub-Permissions:\s*[^\r\n]*' + [regex]::Escape($preflightRequiredPermission)
    $preflightPermissionHeaderPresent = $preflightText -match 'X-Accepted-GitHub-Permissions:'
    $preflightPermissionAccepted = !$preflightPermissionHeaderPresent -or $preflightText -match $preflightPermissionPattern
    $preflightMissingFieldPattern = '(?is)("code"\s*:\s*"missing_field"|missing required keys|must not be blank|can(?:not|''t) be blank|Invalid input:\s*data cannot be null|Validation Failed)'
    $preflightExpected422 = $preflightNativeExitCode -ne 0 -and $preflightText -match '(HTTP(?:/\S+)?\s+422\b|\(HTTP 422\))' -and $preflightPermissionAccepted -and $preflightText -match $preflightMissingFieldPattern
    $preflightExitCode = if ($preflightExpected422) { 0 } elseif ($preflightNativeExitCode -ne 0) { $preflightNativeExitCode } else { 1 }
    Write-Output "GHPMV_PREFLIGHT_DONE:<preflight-id>:$preflightExitCode"
}
finally {
    if ($null -eq $previousPreflightToken) {
        Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue
    }
    else {
        $env:GH_TOKEN = $previousPreflightToken
    }
}
```

`repos` と `issue-fields` を一つの wrapper にまとめず、それぞれ異なる `<preflight-id>` で送り、今回の ID と完全一致する `GHPMV_PREFLIGHT_DONE:<preflight-id>:0` を確認する。wrapper は endpoint から `administration=write` または `issue_fields=write` を選ぶため、permission 名を別途手入力しない。target preflight では `$env:SOURCE_TOKEN` を `$env:TARGET_TOKEN` に置き換える。GitHub CLI は `github.com` と `*.ghe.com` の両方に `GH_TOKEN` を使うため、data residency 側も token variable は変えない。

semantic success は、native command が non-zero、HTTP status が 422、本文が必須 field または必須 request body の不足を示し、fine-grained PATで`X-Accepted-GitHub-Permissions` headerが返る場合はendpointごとの必須permissionと一致する場合だけとする。classic PATではこのheaderが省略されるため、header不在だけをfailureにしない。本文の文言は API version と endpoint により `Validation Failed`、`missing_field`、`missing required keys`、`must not be blank`、`Invalid input: data cannot be null` などに変わり得るため、固定文字列だけを要求しない。transport error、403、422 以外、返されたpermission headerの不一致、または必須入力不足を示さない422はfailureのままにする。data residency 側を確認するときは、両方の `gh api` command に `--hostname TENANT.ghe.com` を追加する。`GH_TOKEN` が cached credentials より優先され、`--hostname` が接続先 tenant を選ぶ。GitHub.com source → data residency target の source preflight には hostname を追加せず、target preflight だけに target tenant hostname を追加する。

どちらも必須 field を渡さないため repository や Issue Field は作成されない。両方が上記の permission header 付き missing-field 422 なら endpoint permission は認識されているため続行できる。repository endpoint が `403 Resource not accessible by personal access token` なら、設定画面で **Administration: Read and write**、**All repositories**、organization approval を再確認する。Issue Field endpoint または GraphQL の `organization.issueFields` が `FORBIDDEN` なら **Organization permissions → Issue Fields: Read and write** (`issue_fields=write`) と、token ownerがorganization administratorであることを確認する。Migrator roleやclassic PATの`admin:org` scopeだけではadministrator roleを代替しない。repository creation policy、organization の PAT restriction、SSOも別に確認し、原因を一つに断定しない。administrator roleとpermissionを満たしたtokenでも403になる場合は`setup --fixture`を実行せず停止する。

repository endpointだけが403で、Issue Field administrator gateは通過している場合は次のどちらかを選んでもらう。

1. 同じorganization administratorのclassic PAT (`repo`, `project`, `admin:org`) に切り替える
2. 空の private repository を先に作り、同じadministratorのfine-grained PATで残りのfixtureを作成する

fine-grained PAT の **Administration** または **All repositories** を付与できない場合だけ、空 repository を先に作成し、その repository を選択したtokenにAdministration以外のfixture権限を付ける。

この fallback を選んだ場合は、選択した side の `empty-repository fallback` だけを `selected` と記録し、repository に commit / file / Issue がないことをユーザーに明示確認する。その side の fixture command に `--fixture-require-new --fixture-allow-existing-empty-repo` を指定する。CLI は新規 Project title を必須のまま維持し、既存 repository の contents と Issue が空であることを読み取り確認してからだけ fixture write を許可する。通常経路と、fallback を選んでいない反対 side には `--fixture-allow-existing-empty-repo` を付けない。

## Step 5: Source fixture

`api-only` または `browser-e2e` で `fixture preparation` が `create` の場合に resource 作成を実行する。`read-only` と通常の `existing` 経路ではスキップし、source resource を作成しない。`browser-e2e` + `existing` では書き込み command を実行せず、source Project number と固定 field-sum contract を記録する確認 Step として実行し、実データの合否は Step 6 で自動判定する。

`browser-e2e` では fixture preparation にかかわらず、Step 5 の開始時に次を state へ記録する。

- View names: `View 1`, `Fixture Board`, `Fixture Roadmap`, `Fixture Empty Sums`
- Number field names: `Fixture Number`, `Fixture Number 2`
- Grouping field: `Status`
- expected FieldSum: session state の fixture contract 表

`fixture preparation=existing` の source Project number が settings から確定済みなら質問しない。未確定ならProject numberだけを一問で確認する。contract は Step 6 の snapshot inspection が機械判定するため、ユーザーへ自己申告を求めず、それまで `browser-e2e field-sum status=fixture-pending` のままにする。

source organization を確定した後、validation run ごとに `yyyyMMdd-HHmmss` 形式の run ID を一度だけ生成し、以後 source / target の resource 名で共用する。

source fixture title と repository name は run ID から自動決定し、質問しない。

- fixture title: `ghpmv E2E source <run-id>`
- repository name: `ghpmv-e2e-source-<run-id>`

実resource作成前の説明で自動決定した実値を明記する。E2E 作成 command では `--fixture-require-new` を必ず指定し、既存 Project title または repository を検出したら書き込み前に失敗させる。

fixture title と repository name は PowerShell の single-quoted argument として渡す。ユーザーが別名を入力した場合は、値に含まれる `'` を `''` に置換してから single quotes で囲む。未 quote の値を command に展開しない。

run ID 付き推奨値では、作成前に GitHub Projects (classic) REST endpoint (`/orgs/{org}/projects`) を使った title 衝突確認を行わない。この endpoint は Projects v2 の確認にならず、HTTP 4xx を「衝突なし」に変換してはならない。fixture command 自体が name conflict を返した場合だけ、resource が作成されていないことを確認し、新しい run ID の推奨値を提示する。任意の preflight command を追加した場合も、non-zero exit code や HTTP error を成功扱いせず、その command の成否を fixture 作成の成否と混同しない。

`api-only`:

```powershell
$previousGhpmvToken = $env:GHPMV_TOKEN
$previousGitHubToken = $env:GITHUB_TOKEN
try {
    $env:GHPMV_TOKEN = $env:SOURCE_TOKEN
    Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue
    dotnet run --project src\Ghpmv.Cli -c Release --no-build -- setup `
      --fixture `
      --fixture-org <source-org> `
      --fixture-title '<escaped-unique-title>' `
      --fixture-repo '<escaped-unique-repo>' `
      --fixture-require-new
}
finally {
    if ($null -eq $previousGhpmvToken) { Remove-Item Env:GHPMV_TOKEN -ErrorAction SilentlyContinue } else { $env:GHPMV_TOKEN = $previousGhpmvToken }
    if ($null -eq $previousGitHubToken) { Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue } else { $env:GITHUB_TOKEN = $previousGitHubToken }
}
```

`browser-e2e` では API fixture と UI fixture を同じ owned operation として実行する。`--fixture-project` を指定した別 command に分けない。

```powershell
$previousGhpmvToken = $env:GHPMV_TOKEN
$previousGitHubToken = $env:GITHUB_TOKEN
try {
    $env:GHPMV_TOKEN = $env:SOURCE_TOKEN
    Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue
    dotnet run --project src\Ghpmv.Cli -c Release --no-build -- setup `
      --fixture `
      --fixture-ui `
      --fixture-org <source-org> `
      --fixture-title '<escaped-unique-title>' `
      --fixture-repo '<escaped-unique-repo>' `
      --fixture-require-new `
      --browser-profile source
}
finally {
    if ($null -eq $previousGhpmvToken) { Remove-Item Env:GHPMV_TOKEN -ErrorAction SilentlyContinue } else { $env:GHPMV_TOKEN = $previousGhpmvToken }
    if ($null -eq $previousGitHubToken) { Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue } else { $env:GITHUB_TOKEN = $previousGitHubToken }
}
```

source が data residency の場合は選択した source command に `--api-base-url <source-api-url>` を追加し、`browser-e2e` ではさらに `--browser-base-url <source-web-url>` を追加する。GitHub.com source ではどちらも付けない。

`source empty-repository fallback` が `selected` の場合だけ、上記 source command に次も追加する。

```powershell
--fixture-allow-existing-empty-repo
```

出力された source Project number を記録する。

`fixture preparation=create` の成功後、出力された source Project title / number / URL を resource inventory に `created` として追加する。`source empty-repository fallback=selected` なら `<source-org>/<source-repo>` はこの run より前に作成されたため `pre-existing` として追加し、通常経路だけ repository を `created` とする。`browser-e2e` では作成された source fixture が上記 contract を持つことを前提にせず、Step 6 の gate で必ず確認する。

`browser-e2e` の再試行も同じ combined command と同じ title / repository を使う。CLI は owned fixture の `fixture-ui-complete` marker を確認し、完了済みなら UI setup を自動で skipし、未完了なら再開する。marker-aware retry を迂回するため、通常の再試行で `--fixture-ui --fixture-project <source-project-number>` を実行しない。

### Fixture UI 再実行

同じ Project に明示的に再実行すると non-default Views が重複する。次のどちらかを選んでもらう。

1. 新しい fixture Project を作る（推奨）
2. `View 1` を残し、既存の `Fixture Board` / `Fixture Roadmap` / `Fixture Empty Sums` を手動削除して再実行する

Workflow は再設定できる。warning が出た場合は、目視だけで終了せず、後続 export が UI settings を警告なしで取得できるか確認する。

## Step 6: Source export

再実行時は新しい directory を使う。mapping CSV は既存ファイルを上書きしない。

`read-only` と `api-only` では browser option を付けない。

```powershell
$env:GHPMV_DEMO_SNAPSHOT = Join-Path $env:TEMP "ghpmv-demo-snapshot-$(Get-Date -Format yyyyMMdd-HHmmss)"
$previousGhpmvToken = $env:GHPMV_TOKEN
$previousGitHubToken = $env:GITHUB_TOKEN
try {
    $env:GHPMV_TOKEN = $env:SOURCE_TOKEN
    Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue
    dotnet run --project src\Ghpmv.Cli -c Release --no-build -- export `
      --org <source-org> `
      --project <source-project-number> `
      --out $env:GHPMV_DEMO_SNAPSHOT
}
finally {
    if ($null -eq $previousGhpmvToken) { Remove-Item Env:GHPMV_TOKEN -ErrorAction SilentlyContinue } else { $env:GHPMV_TOKEN = $previousGhpmvToken }
    if ($null -eq $previousGitHubToken) { Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue } else { $env:GITHUB_TOKEN = $previousGitHubToken }
}
```

`browser-e2e` では同じ export に browser option を追加する。

```powershell
$env:GHPMV_DEMO_SNAPSHOT = Join-Path $env:TEMP "ghpmv-demo-snapshot-$(Get-Date -Format yyyyMMdd-HHmmss)"
$previousGhpmvToken = $env:GHPMV_TOKEN
$previousGitHubToken = $env:GITHUB_TOKEN
try {
    $env:GHPMV_TOKEN = $env:SOURCE_TOKEN
    Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue
    dotnet run --project src\Ghpmv.Cli -c Release --no-build -- export `
      --org <source-org> `
      --project <source-project-number> `
      --out $env:GHPMV_DEMO_SNAPSHOT `
      --enable-browser-automation `
      --browser-profile source
}
finally {
    if ($null -eq $previousGhpmvToken) { Remove-Item Env:GHPMV_TOKEN -ErrorAction SilentlyContinue } else { $env:GHPMV_TOKEN = $previousGhpmvToken }
    if ($null -eq $previousGitHubToken) { Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue } else { $env:GITHUB_TOKEN = $previousGitHubToken }
}
```

source が data residency の場合は、browser option の有無にかかわらず `--base-url <source-api-url>` を追加し、browser automation を使う場合はさらに `--browser-base-url <source-web-url>` を追加する。`github.com-to-ghec-dr` の source export にはこれらを付けない。

確認するもの:

- `snapshot.json`
- `repository-mappings.csv`
- `organization-mappings.csv`
- 必要な場合 `user-mappings.csv`
- View / Workflow / collaborator warning

warning がある場合、どの UI-only field が欠落したかを示して続行可否を確認する。

### Field sum snapshot gate

`browser-e2e` では export 成功直後、`requirements` や target resource 準備より前に、同じ token execution terminal で `snapshot.json` 自体を検査する。次の PowerShell を一つの command として送り、通常の一意な completion sentinel で監視する。

```powershell
function Stop-FieldSumSnapshotCheck([string]$Message) {
    Write-Error $Message
    $global:LASTEXITCODE = 1
}
$snapshotPath = Join-Path $env:GHPMV_DEMO_SNAPSHOT 'snapshot.json'
if (!(Test-Path -LiteralPath $snapshotPath)) { Stop-FieldSumSnapshotCheck "snapshot.json was not found: $snapshotPath"; return }
$snapshot = Get-Content -LiteralPath $snapshotPath -Raw | ConvertFrom-Json
$expectedViews = @(
    [pscustomobject]@{ Name = 'View 1'; Layout = 'TABLE_LAYOUT'; GroupBy = @('Status'); FieldSum = @('Count', 'Fixture Number', 'Fixture Number 2') },
    [pscustomobject]@{ Name = 'Fixture Board'; Layout = 'BOARD_LAYOUT'; GroupBy = @('Status'); FieldSum = @('Fixture Number') },
    [pscustomobject]@{ Name = 'Fixture Roadmap'; Layout = 'ROADMAP_LAYOUT'; GroupBy = @('Status'); FieldSum = @('Fixture Number 2') },
    [pscustomobject]@{ Name = 'Fixture Empty Sums'; Layout = 'TABLE_LAYOUT'; GroupBy = @('Status'); FieldSum = @() }
)
foreach ($requiredField in @('Fixture Number', 'Fixture Number 2')) {
    $numberFields = @($snapshot.fields | Where-Object { $_.name -eq $requiredField -and $_.dataType -eq 'NUMBER' })
    if ($numberFields.Count -ne 1) { Stop-FieldSumSnapshotCheck "Required NUMBER field '$requiredField' was not found exactly once in snapshot.json."; return }
}
foreach ($expected in $expectedViews) {
    $matches = @($snapshot.views | Where-Object name -eq $expected.Name)
    if ($matches.Count -ne 1) { Stop-FieldSumSnapshotCheck "Expected exactly one view '$($expected.Name)', found $($matches.Count)."; return }
    $actual = $matches[0]
    if ($actual.layout -ne $expected.Layout) { Stop-FieldSumSnapshotCheck "View '$($expected.Name)' layout mismatch: expected $($expected.Layout), actual $($actual.layout)."; return }
    if ($null -eq $actual.ui) { Stop-FieldSumSnapshotCheck "View '$($expected.Name)' is missing browser-captured UI settings."; return }
    $actualGroupBy = @($actual.groupByFields)
    $actualFieldSum = @($actual.ui.fieldSum)
    $expectedGroupBy = @($expected.GroupBy)
    $expectedFieldSum = @($expected.FieldSum)
    $groupByDifference = @(Compare-Object -ReferenceObject $expectedGroupBy -DifferenceObject $actualGroupBy -CaseSensitive)
    if ($actualGroupBy.Count -ne $expectedGroupBy.Count -or $groupByDifference.Count -ne 0) {
        Stop-FieldSumSnapshotCheck "View '$($expected.Name)' groupBy mismatch: expected [$($expected.GroupBy -join ', ')], actual [$($actualGroupBy -join ', ')]."
        return
    }
    $fieldSumDifference = @(Compare-Object -ReferenceObject $expectedFieldSum -DifferenceObject $actualFieldSum -CaseSensitive)
    if ($actualFieldSum.Count -ne $expectedFieldSum.Count -or $fieldSumDifference.Count -ne 0) {
        Stop-FieldSumSnapshotCheck "View '$($expected.Name)' FieldSum mismatch: expected [$($expected.FieldSum -join ', ')], actual [$($actualFieldSum -join ', ')]."
        return
    }
    Write-Output ("GHPMV_FIELD_SUM_VIEW:{0}:{1}" -f $expected.Name, ($actualFieldSum -join ', '))
}
Write-Output 'GHPMV_FIELD_SUM_SNAPSHOT_MATCH'
$global:LASTEXITCODE = 0
```

`GHPMV_FIELD_SUM_SNAPSHOT_MATCH` と command exit code 0 の両方を確認した場合だけ `browser-e2e field-sum status=snapshot-match` とし、先へ進む。Table / Roadmap のいずれかだけ一致、warning、missing UI、`1 more` のような summary text、`null` と空集合以外の不一致を成功扱いしない。失敗時は source fixture contract の実値を示し、新しい標準 fixture を作るかどうかを一問で確認して停止する。

`api-only` / `browser-e2e`では、target PAT入力またはtarget resource準備より前に同じterminalでsnapshot-driven capabilityを算出する。

```powershell
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- requirements --in $env:GHPMV_DEMO_SNAPSHOT
```

`browser-e2e`では`--enable-browser-automation`を追加する。`api-only`では追加せず、UI-only Workflow/filter repository要件を要求しない。

exit code 0と全出力をagentが確認し、次をstateへ記録する。

- `requires-organization-administrator=true`: target token/browser accountをorganization administratorに限定する。
- `requires-project-administrator=true`: collaborator replay前にtarget Project adminであることを要求する。
- `requires-members-read=true`: target token planへMembers readを追加する。
- `requires-visibility-management=true`: target organizationのvisibility policyとtarget Project admin/owner権限を確認する。
- `repository=... capabilities=...`: 全source repository candidateをmappingへ残し、Issues/PullRequests read、Issues write、Contents write、same-owner、browser accessをtarget token/browser planへ反映する。

`fixture preparation=existing`で延期していたtarget/GEI token phaseはこの出力後に開始する。必要role/access/policyが未確認ならPAT入力へ進まず、target accountやmappingを確定してからだけStep 7へ進む。

`read-only` はここで完了報告を行い、終了する。target resource の準備、mapping の編集、import、verify は案内しない。

## Step 7: Target repository を準備する

`api-only` と `browser-e2e` だけが実行する。

Step 1 で記録した `repository preparation mode` の経路だけを実行する。

### GEI

この経路へ入る前にsource / target hostと記録済みAPI URLを再確認する。data-residency sourceでは`--github-source-api-url`、data-residency targetでは`--target-api-url`と`--target-uploads-url`を必ず含める。

settings の `gei.repositoryMigrationsBypass` を確認する。`exempt` または `not-applicable` なら質問せず進む。`unconfirmed` なら、destination の applicable ruleset で **Repository migrations** bypass を **Exempt** にするか、applicable rulesetがないことをsettingsへ記録するまで停止する。既定の **Always allow** のまま進めない。

`fixture preparation=create` では source repository に `ghpmv-e2e-source-<run-id>`、target repository に `ghpmv-e2e-target-<run-id>` を自動使用し、名前を質問しない。`fixture preparation=existing` の場合だけ settings の `gei.sourceRepository` / `gei.targetRepository` を使う。実resource作成前の説明には解決済みの full name を表示する。

`gh gei migrate-repo --help` で extension の現在の引数を確認した後、選択topologyに応じて次を設定する。

- GitHub.com source: `$sourceApiUrl = $null`
- data-residency source: `$sourceApiUrl = '<source-api-url>'`
- GitHub.com target: `$targetApiUrl = $null`, `$targetUploadsUrl = $null`
- data-residency target: `$targetApiUrl = '<target-api-url>'`, `$targetUploadsUrl = '<target-uploads-url>'`

```powershell
$sourceApiUrl = <resolved-source-api-url-or-$null>
$targetApiUrl = <resolved-target-api-url-or-$null>
$targetUploadsUrl = <resolved-target-uploads-url-or-$null>
$previousGeiSourcePat = $env:GH_SOURCE_PAT
$previousGeiTargetPat = $env:GH_PAT
try {
    $env:GH_SOURCE_PAT = $env:GHPMV_GEI_SOURCE_TOKEN
    $env:GH_PAT = $env:GHPMV_GEI_TARGET_TOKEN
    $geiArguments = @(
        'migrate-repo',
        '--github-source-org', '<source-org>',
        '--source-repo', '<source-repo>',
        '--github-target-org', '<target-org>',
        '--target-repo', '<target-repo>',
        '--target-repo-visibility', 'private'
    )
    if ($null -ne $sourceApiUrl) {
        $geiArguments += @('--github-source-api-url', $sourceApiUrl)
    }
    if ($null -ne $targetApiUrl) {
        $geiArguments += @('--target-api-url', $targetApiUrl, '--target-uploads-url', $targetUploadsUrl)
    }
    & gh gei @geiArguments
}
finally {
    if ($null -eq $previousGeiSourcePat) { Remove-Item Env:GH_SOURCE_PAT -ErrorAction SilentlyContinue } else { $env:GH_SOURCE_PAT = $previousGeiSourcePat }
    if ($null -eq $previousGeiTargetPat) { Remove-Item Env:GH_PAT -ErrorAction SilentlyContinue } else { $env:GH_PAT = $previousGeiTargetPat }
}
```

placeholderとresolved URL変数を記録済み実値へ置き換え、commandごとの一意なIDを付けたwrapperで同じterminal sessionに送信し、exit codeとmigration completionを監視する。PAT optionは追加せず、`GH_SOURCE_PAT`と`GH_PAT`のprocess environment経由だけで渡す。GitHubの[`gh-gei` data-residency source usage](https://github.com/github/gh-gei#github-to-github-usage-githubcom---githubcom)とdata-residency target手順に従い、source / destination organizationとtenant endpoint、tenant固有のIP allow listを確認する。

target repository full name を記録し、target repository name / URL / creation Step を resource inventory に `created` として追加する。まずexport済みsnapshotから、移行対象repositoryのsource Issue / PR numberを同じterminalで列挙する。

```powershell
$snapshot = Get-Content -LiteralPath (Join-Path $env:GHPMV_DEMO_SNAPSHOT 'snapshot.json') -Raw | ConvertFrom-Json
$sourceRepositoryItems = @($snapshot.items | Where-Object { $_.type -in @('ISSUE', 'PULL_REQUEST') } | Select-Object type, repository, number)
if ($sourceRepositoryItems.Count -eq 0) { throw 'The source snapshot contains no Issue or Pull Request item to validate after GEI.' }
$sourceRepositoryItems | Format-Table -AutoSize
```

続けてsource item一件ごとに、`ISSUE` は `issues/<number>`、`PULL_REQUEST` は `pulls/<number>`へ置き換え、次のcommandを別々の一意なcommand IDで送る。このqueryにはGEI tokenではなく、後続import/verifyで使用する`TARGET_TOKEN`を使うため、fine-grained PATの新規repository accessも同時に確認できる。

```powershell
$previousTargetCheckToken = $env:GH_TOKEN
try {
    $env:GH_TOKEN = $env:TARGET_TOKEN
    gh api "repos/<target-org>/<target-repo>/<issues-or-pulls>/<source-number>" --jq '.number'
}
finally {
    if ($null -eq $previousTargetCheckToken) { Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue } else { $env:GH_TOKEN = $previousTargetCheckToken }
}
```

data residency targetでは`gh api`に`--hostname TENANT.ghe.com`を追加する。出力numberがsource numberと一致し、全queryがexit code 0の場合だけnumber維持と`TARGET_TOKEN`のrepository accessを確認済みとする。404または`Resource not accessible by personal access token`の場合は、migration失敗と断定せず、まずtarget repositoryの存在とfine-grained PATのRepository access / approvalを確認する。target Issue / PR number一致とtoken accessを確認できるまでStep 8へ進まない。

downloadable migration log は完了後 24 時間以内に保存する。target repository の Issues が無効なら `Migration Log` Issue は作成されない。

### Fixture seed

`ghpmv` 自体の短時間デモ用であり、GEI の検証にはならず、補助 Project が一つ増えることを説明してから実行する。

target seed title と repository name もrun IDから自動決定し、質問しない。Step 5 で生成した run ID があれば同じ値を使う。`fixture preparation` が `existing` で Step 5 をスキップしたなど run ID がまだない場合は、ここで `yyyyMMdd-HHmmss` 形式の run ID を一度だけ生成して記録する。

- target seed title: `ghpmv E2E target seed <run-id>`
- target repository name: `ghpmv-e2e-target-<run-id>`

実resource作成前の説明で自動決定した実値を明記する。`--fixture-require-new` により既存 Project title または repository を書き込み前に検出し、明示的な error で停止する。Projects (classic) REST endpoint による事前確認は行わない。

target seed title と repository name も `'` を `''` に置換したうえで PowerShell single-quoted argument として渡す。

```powershell
$previousGhpmvToken = $env:GHPMV_TOKEN
$previousGitHubToken = $env:GITHUB_TOKEN
try {
    $env:GHPMV_TOKEN = $env:TARGET_TOKEN
    Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue
    dotnet run --project src\Ghpmv.Cli -c Release --no-build -- setup `
      --fixture `
      --fixture-org <target-org> `
      --fixture-title '<escaped-unique-target-seed-title>' `
      --fixture-repo '<escaped-target-repo>' `
      --fixture-require-new
}
finally {
    if ($null -eq $previousGhpmvToken) { Remove-Item Env:GHPMV_TOKEN -ErrorAction SilentlyContinue } else { $env:GHPMV_TOKEN = $previousGhpmvToken }
    if ($null -eq $previousGitHubToken) { Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue } else { $env:GITHUB_TOKEN = $previousGitHubToken }
}
```

target が data residency の場合は `--api-base-url <target-api-url>` を追加する。

`target empty-repository fallback` が `selected` の場合だけ、上記 target command に `--fixture-allow-existing-empty-repo` も追加する。

fixture seed 成功後、出力された target seed Project title / number / URL を resource inventory に `created` として追加する。`target empty-repository fallback=selected` なら `<target-org>/<target-repo>` はこの run より前に作成されたため `pre-existing` として追加し、通常経路だけ repository を `created` とする。import 先 Project とは別 entry にする。

target 側の `setup --fixture-ui` は不要。

## Step 8: Mapping を完成させる

`api-only` と `browser-e2e` だけが実行する。

生成済み CSV を必ず読み、空の target 値を列挙する。固定の一行だけで置き換えない。

- `repository-mappings.csv`: full name と short name の候補を含め、全行を target `owner/repo` へ対応付ける。
- `organization-mappings.csv`: source owner を target owner へ対応付ける。
- `user-mappings.csv`: source login / mannequin user を実 target login へ対応付ける。

まず同じ terminal で次を実行し、存在する mapping file の全行と空 target を agent 自身が読む。生成されなかった optional file はエラーにしない。

```powershell
$mappingFiles = Get-ChildItem -LiteralPath $env:GHPMV_DEMO_SNAPSHOT -Filter '*-mappings.csv' -File
if ($mappingFiles.Count -eq 0) { throw "No mapping CSV files were generated in $env:GHPMV_DEMO_SNAPSHOT" }
foreach ($mappingFile in $mappingFiles) {
    Write-Output ("--- {0} ---" -f $mappingFile.Name)
    Get-Content -LiteralPath $mappingFile.FullName
    $targetColumn = if ($mappingFile.Name -eq 'user-mappings.csv') { 'target-user' } else { 'target' }
    $sourceColumn = if ($mappingFile.Name -eq 'user-mappings.csv') { 'mannequin-user' } else { 'source' }
    foreach ($row in @(Import-Csv -LiteralPath $mappingFile.FullName)) {
        if ([string]::IsNullOrWhiteSpace($row.$targetColumn)) {
            Write-Output ("GHPMV_MAPPING_BLANK:{0}:{1}" -f $mappingFile.Name, $row.$sourceColumn)
        }
    }
}
```

target login は token 値ではなくユーザー名だけを確認する。Browser account と token owner が同じで、すでに記録済みなら再質問しない。EMU suffix を省略しない。

標準 fixture の単一 repository を GEI または fixture seed で用意した経路では、全 repository candidate は記録済みの同じ target repository、全 organization candidate は target organization、全 user candidate は確認済み target login へ対応する。次の placeholder を記録済み実値へ置き換え、一 command として送る。既に埋まっている target は上書きしない。GitHub login / organization / repository 名に comma が含まれていた場合はCSVを壊さず停止する。

```powershell
$targetRepository = '<target-org>/<target-repo>'
$targetOrganization = '<target-org>'
$targetUser = '<target-login>'
$repoPath = Join-Path $env:GHPMV_DEMO_SNAPSHOT 'repository-mappings.csv'
$orgPath = Join-Path $env:GHPMV_DEMO_SNAPSHOT 'organization-mappings.csv'
$userPath = Join-Path $env:GHPMV_DEMO_SNAPSHOT 'user-mappings.csv'
if (!(Test-Path -LiteralPath $repoPath) -or !(Test-Path -LiteralPath $orgPath)) { throw 'Required repository or organization mapping CSV is missing.' }
if (@($targetRepository, $targetOrganization, $targetUser) | Where-Object { $_ -match ',' }) { throw 'Mapping targets must not contain commas.' }
$repoLines = @('source,target')
foreach ($row in @(Import-Csv -LiteralPath $repoPath)) {
    $target = if ([string]::IsNullOrWhiteSpace($row.target)) { $targetRepository } else { $row.target }
    $repoLines += '{0},{1}' -f $row.source, $target
}
Set-Content -LiteralPath $repoPath -Value $repoLines -Encoding UTF8
$orgLines = @('source,target')
foreach ($row in @(Import-Csv -LiteralPath $orgPath)) {
    $target = if ([string]::IsNullOrWhiteSpace($row.target)) { $targetOrganization } else { $row.target }
    $orgLines += '{0},{1}' -f $row.source, $target
}
Set-Content -LiteralPath $orgPath -Value $orgLines -Encoding UTF8
if (Test-Path -LiteralPath $userPath) {
    $userLines = @('mannequin-user,mannequin-id,target-user')
    foreach ($row in @(Import-Csv -LiteralPath $userPath)) {
        $target = if ([string]::IsNullOrWhiteSpace($row.'target-user')) { $targetUser } else { $row.'target-user' }
        $userLines += '{0},{1},{2}' -f $row.'mannequin-user', $row.'mannequin-id', $target
    }
    Set-Content -LiteralPath $userPath -Value $userLines -Encoding UTF8
}
```

標準 fixture 以外で複数の target repository / user が必要な場合は、最初の inspection で出た `GHPMV_MAPPING_BLANK` ごとに一つずつ mapping 先を質問し、同じ plain CSV header を維持する等価 command を生成する。一律に同じ target へ置換しない。

編集後は同じ terminal で次を実行する。空 target、header 不一致、空 source が一つでもあれば Import へ進まない。

```powershell
$remaining = @()
foreach ($mappingFile in Get-ChildItem -LiteralPath $env:GHPMV_DEMO_SNAPSHOT -Filter '*-mappings.csv' -File) {
    $targetColumn = if ($mappingFile.Name -eq 'user-mappings.csv') { 'target-user' } else { 'target' }
    $sourceColumn = if ($mappingFile.Name -eq 'user-mappings.csv') { 'mannequin-user' } else { 'source' }
    foreach ($row in @(Import-Csv -LiteralPath $mappingFile.FullName)) {
        if ([string]::IsNullOrWhiteSpace($row.$sourceColumn) -or [string]::IsNullOrWhiteSpace($row.$targetColumn)) {
            $remaining += '{0}:{1}' -f $mappingFile.Name, $row.$sourceColumn
        }
    }
    Write-Output ("--- {0} ---" -f $mappingFile.Name)
    Get-Content -LiteralPath $mappingFile.FullName
}
if ($remaining.Count -gt 0) { throw ('Incomplete mapping rows: ' + ($remaining -join ', ')) }
Write-Output 'GHPMV_MAPPINGS_COMPLETE'
```

## Step 9: Import

`api-only` と `browser-e2e` だけが実行する。

存在する mapping file をすべて渡す。

`api-only` では browser option を付けない。

```powershell
$previousGhpmvToken = $env:GHPMV_TOKEN
$previousGitHubToken = $env:GITHUB_TOKEN
try {
    $env:GHPMV_TOKEN = $env:TARGET_TOKEN
    Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue
    dotnet run --project src\Ghpmv.Cli -c Release --no-build -- import `
      --org <target-org> `
      --in $env:GHPMV_DEMO_SNAPSHOT `
      --repo-mapping "$env:GHPMV_DEMO_SNAPSHOT\repository-mappings.csv" `
      --user-mapping "$env:GHPMV_DEMO_SNAPSHOT\user-mappings.csv" `
      --org-mapping "$env:GHPMV_DEMO_SNAPSHOT\organization-mappings.csv"
}
finally {
    if ($null -eq $previousGhpmvToken) { Remove-Item Env:GHPMV_TOKEN -ErrorAction SilentlyContinue } else { $env:GHPMV_TOKEN = $previousGhpmvToken }
    if ($null -eq $previousGitHubToken) { Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue } else { $env:GITHUB_TOKEN = $previousGitHubToken }
}
```

target が data residency の場合は `--target-base-url <target-api-url>` を追加する。GitHub.com target では付けない。

`browser-e2e` では同じ import に browser option を追加する。

```powershell
$previousGhpmvToken = $env:GHPMV_TOKEN
$previousGitHubToken = $env:GITHUB_TOKEN
try {
    $env:GHPMV_TOKEN = $env:TARGET_TOKEN
    Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue
    dotnet run --project src\Ghpmv.Cli -c Release --no-build -- import `
      --org <target-org> `
      --in $env:GHPMV_DEMO_SNAPSHOT `
      --repo-mapping "$env:GHPMV_DEMO_SNAPSHOT\repository-mappings.csv" `
      --user-mapping "$env:GHPMV_DEMO_SNAPSHOT\user-mappings.csv" `
      --org-mapping "$env:GHPMV_DEMO_SNAPSHOT\organization-mappings.csv" `
      --enable-browser-automation `
      --browser-profile target
}
finally {
    if ($null -eq $previousGhpmvToken) { Remove-Item Env:GHPMV_TOKEN -ErrorAction SilentlyContinue } else { $env:GHPMV_TOKEN = $previousGhpmvToken }
    if ($null -eq $previousGitHubToken) { Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue } else { $env:GITHUB_TOKEN = $previousGitHubToken }
}
```

target が data residency の場合は `--target-base-url <target-api-url>` と `--browser-base-url <target-web-url>` を追加する。`github.com-to-ghec-dr` ではこの target command にだけ両方を付ける。

生成されなかった optional mapping file の引数だけを外す。出力の `result` と target Project title / number / URL を記録し、import が新規 Project を作成した場合は resource inventory に `created` として追加する。既存 Project を更新した場合は `pre-existing` として記録し cleanup 対象にしない。`browser-e2e` では View / Workflow browser warning が一つでもあれば成功扱いせず、その property と View 名を示して停止する。

## Step 10: Verify

`api-only` と `browser-e2e` だけが実行する。

Import と同じ mapping / browser profile を渡す。

`api-only` では browser option を付けない。

```powershell
$previousGhpmvToken = $env:GHPMV_TOKEN
$previousGitHubToken = $env:GITHUB_TOKEN
try {
    $env:GHPMV_TOKEN = $env:TARGET_TOKEN
    Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue
    dotnet run --project src\Ghpmv.Cli -c Release --no-build -- verify `
      --org <target-org> `
      --project <target-project-number> `
      --in $env:GHPMV_DEMO_SNAPSHOT `
      --repo-mapping "$env:GHPMV_DEMO_SNAPSHOT\repository-mappings.csv" `
      --user-mapping "$env:GHPMV_DEMO_SNAPSHOT\user-mappings.csv" `
      --org-mapping "$env:GHPMV_DEMO_SNAPSHOT\organization-mappings.csv" `
      --report-json "$env:GHPMV_DEMO_SNAPSHOT\verify-report.json"
}
finally {
    if ($null -eq $previousGhpmvToken) { Remove-Item Env:GHPMV_TOKEN -ErrorAction SilentlyContinue } else { $env:GHPMV_TOKEN = $previousGhpmvToken }
    if ($null -eq $previousGitHubToken) { Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue } else { $env:GITHUB_TOKEN = $previousGitHubToken }
}
```

target が data residency の場合は `--target-base-url <target-api-url>` を追加する。

`browser-e2e` では同じ verify に browser option を追加する。

```powershell
$previousGhpmvToken = $env:GHPMV_TOKEN
$previousGitHubToken = $env:GITHUB_TOKEN
try {
    $env:GHPMV_TOKEN = $env:TARGET_TOKEN
    Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue
    dotnet run --project src\Ghpmv.Cli -c Release --no-build -- verify `
      --org <target-org> `
      --project <target-project-number> `
      --in $env:GHPMV_DEMO_SNAPSHOT `
      --repo-mapping "$env:GHPMV_DEMO_SNAPSHOT\repository-mappings.csv" `
      --user-mapping "$env:GHPMV_DEMO_SNAPSHOT\user-mappings.csv" `
      --org-mapping "$env:GHPMV_DEMO_SNAPSHOT\organization-mappings.csv" `
      --enable-browser-automation `
      --browser-profile target `
      --fail-on-warning `
      --report-json "$env:GHPMV_DEMO_SNAPSHOT\verify-report.json"
}
finally {
    if ($null -eq $previousGhpmvToken) { Remove-Item Env:GHPMV_TOKEN -ErrorAction SilentlyContinue } else { $env:GHPMV_TOKEN = $previousGhpmvToken }
    if ($null -eq $previousGitHubToken) { Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue } else { $env:GITHUB_TOKEN = $previousGitHubToken }
}
```

target が data residency の場合は `--target-base-url <target-api-url>` と `--browser-base-url <target-web-url>` を追加する。Import と Verify で同じ target endpoint と browser profile を使う。

結果を category ごとに確認する。

- `Match`: 成功
- `PartialMatch`: warning の内容と許容理由を記録
- `Mismatch`: 差分を直して再検証
- `NotVerified`: 必要データが capture できていないため成功扱いにしない

`browser-e2e` では generic な overall 結果確認に加え、同じ terminal で report file を検査する。

```powershell
function Stop-BrowserViewCheck([string]$Message) {
    Write-Error $Message
    $global:LASTEXITCODE = 1
}
$reportPath = Join-Path $env:GHPMV_DEMO_SNAPSHOT 'verify-report.json'
if (!(Test-Path -LiteralPath $reportPath)) { Stop-BrowserViewCheck "verify-report.json was not found: $reportPath"; return }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
$viewCategories = @($report.categories | Where-Object category -eq 'View')
if ($viewCategories.Count -ne 1) { Stop-BrowserViewCheck "Expected exactly one View category, found $($viewCategories.Count)."; return }
if ($viewCategories[0].status -ne 'Match') { Stop-BrowserViewCheck "View category must be Match, but was $($viewCategories[0].status)."; return }
$viewDifferences = @($report.differences | Where-Object category -eq 'View')
if ($viewDifferences.Count -ne 0) { Stop-BrowserViewCheck "View category reported differences despite Match: $($viewDifferences.message -join '; ')"; return }
Write-Output 'GHPMV_BROWSER_VIEW_MATCH'
$global:LASTEXITCODE = 0
```

`GHPMV_BROWSER_VIEW_MATCH` と command exit code 0 を確認した場合だけ `browser-e2e field-sum status=target-view-match` とする。View の warning、`PartialMatch`、`NotVerified` は、overall status が許容可能でも `browser-e2e` の成功にしない。

### Browser field-sum machine check

初回 `View: Match` は browser-assisted exporter が target の各 View を Playwright で再読し、次を source snapshot と機械比較した結果である。

- `View 1`: layout、Group by=`Status`、Field sum=`Count`, `Fixture Number`, `Fixture Number 2`
- `Fixture Roadmap`: layout、Group by=`Status`、Field sum=`Fixture Number 2`
- `Fixture Board`: layout、Swimlanes=`Status`、Field sum=`Fixture Number`
- `Fixture Empty Sums`: layout、Group by=`Status`、Field sum=empty

このため Group by、Field sum menu の選択状態、空集合について対話用質問や目視確認を重ねない。ただし Issue #62 の acceptance criteria にある派生描画の確認は別 checkpoint として一度だけ実行する。初回 `View: Match` 後に target の `View 1` と `Fixture Roadmap` を reload し、各 group header に設定済みの Field sum label と数値が表示されていることを目視確認する。menu は再確認しない。確認結果は screenshot path または簡潔な observation として execution record に残す。

agent が browser 表示を直接観測できない場合だけ、target Project URL と対象 View 名を示し、一つの対話用質問で `View 1` と `Fixture Roadmap` の両方を確認してもらう。確認できた場合だけ `browser-e2e field-sum status=target-render-observed` として deliberate drift へ進む。表示欠落、値欠落、Cancel / Skipped は成功扱いせず pause する。この checkpoint は GitHub の派生描画確認に限定し、既に機械検証済みの Group by / Field sum selection の自己申告を求めない。

### Deliberate drift と repair

初回の機械的な `View: Match` 後、同じ terminal で Playwright による deliberate drift command を送る。ユーザーへ手動変更を依頼しない。

```powershell
$previousGhpmvToken = $env:GHPMV_TOKEN
$previousGitHubToken = $env:GITHUB_TOKEN
try {
    $env:GHPMV_TOKEN = $env:TARGET_TOKEN
    Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue
    dotnet run --project src\Ghpmv.Cli -c Release --no-build -- setup `
      --fixture-field-sum-drift `
      --fixture-org <target-org> `
      --fixture-project <target-project-number> `
      --fixture-repo <target-repository-short-name> `
      --browser-profile target
}
finally {
    if ($null -eq $previousGhpmvToken) { Remove-Item Env:GHPMV_TOKEN -ErrorAction SilentlyContinue } else { $env:GHPMV_TOKEN = $previousGhpmvToken }
    if ($null -eq $previousGitHubToken) { Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue } else { $env:GITHUB_TOKEN = $previousGitHubToken }
}
```

target が data residency の場合は `--api-base-url <target-api-url>` と `--browser-base-url <target-web-url>` を追加する。`Fixture field-sum drift applied`、`viewWarnings=0`、command exit code 0 を確認した後、同じ terminal で次の drift verify command を送る。placeholder、optional mapping、profile、endpoint は初回 verify と同じ実値へ置き換える。この command は native exit code 0 を失敗とし、非ゼロ終了かつ report の View category が `Mismatch`、`field sum mismatch` が存在する場合だけ semantic success とする。

```powershell
function Stop-FieldSumDriftCheck([string]$Message) {
    Write-Error $Message
    $global:LASTEXITCODE = 1
}
$driftReportPath = Join-Path $env:GHPMV_DEMO_SNAPSHOT 'field-sum-drift-report.json'
Remove-Item -LiteralPath $driftReportPath -ErrorAction SilentlyContinue
$previousGhpmvToken = $env:GHPMV_TOKEN
$previousGitHubToken = $env:GITHUB_TOKEN
$driftNativeExitCode = 0
try {
    $env:GHPMV_TOKEN = $env:TARGET_TOKEN
    Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue
    dotnet run --project src\Ghpmv.Cli -c Release --no-build -- verify `
      --org <target-org> `
      --project <target-project-number> `
      --in $env:GHPMV_DEMO_SNAPSHOT `
      --repo-mapping "$env:GHPMV_DEMO_SNAPSHOT\repository-mappings.csv" `
      --user-mapping "$env:GHPMV_DEMO_SNAPSHOT\user-mappings.csv" `
      --org-mapping "$env:GHPMV_DEMO_SNAPSHOT\organization-mappings.csv" `
      --enable-browser-automation `
      --browser-profile target `
      --fail-on-warning `
      --report-json $driftReportPath
    $driftNativeExitCode = $LASTEXITCODE
}
finally {
    if ($null -eq $previousGhpmvToken) { Remove-Item Env:GHPMV_TOKEN -ErrorAction SilentlyContinue } else { $env:GHPMV_TOKEN = $previousGhpmvToken }
    if ($null -eq $previousGitHubToken) { Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue } else { $env:GITHUB_TOKEN = $previousGitHubToken }
}
if ($driftNativeExitCode -eq 0) { Stop-FieldSumDriftCheck 'Deliberate field-sum drift was not detected; verify unexpectedly succeeded.'; return }
if (!(Test-Path -LiteralPath $driftReportPath)) { Stop-FieldSumDriftCheck "Drift report was not created: $driftReportPath"; return }
$driftReport = Get-Content -LiteralPath $driftReportPath -Raw | ConvertFrom-Json
$driftViewCategories = @($driftReport.categories | Where-Object category -eq 'View')
$fieldSumDifferences = @($driftReport.differences | Where-Object { $_.category -eq 'View' -and $_.message -match "view 'View 1': field sum mismatch" })
$nonInfoDifferences = @($driftReport.differences | Where-Object severity -ne 'Info')
$unexpectedCategoryStatuses = @($driftReport.categories | Where-Object {
    $_.category -ne 'View' -and $_.status -notin @('Match', 'NotApplicable')
})
if ($driftViewCategories.Count -ne 1 -or
    $driftViewCategories[0].status -ne 'Mismatch' -or
    $fieldSumDifferences.Count -ne 1 -or
    $nonInfoDifferences.Count -ne 1 -or
    $nonInfoDifferences[0].category -ne 'View' -or
    $nonInfoDifferences[0].message -ne $fieldSumDifferences[0].message -or
    $unexpectedCategoryStatuses.Count -ne 0) {
    Stop-FieldSumDriftCheck 'Verify did not contain exactly the expected View 1 field-sum mismatch.'
    return
}
Write-Output $fieldSumDifferences.message
Write-Output 'GHPMV_FIELD_SUM_DRIFT_DETECTED'
$global:LASTEXITCODE = 0
```

target が data residency の場合は、この drift verify にも初回 verify と同じ `--target-base-url <target-api-url>` と `--browser-base-url <target-web-url>` を追加する。`GHPMV_FIELD_SUM_DRIFT_DETECTED` と wrapper exit code 0 を確認した場合だけ `browser-e2e field-sum status=drift-detected` とする。

続けて同じ snapshot と target Project へ browser-assisted import を再実行する。`--project-number` は既存 Project を常に更新するため、`--on-conflict` や `--project-title` を追加しない。

```powershell
$previousGhpmvToken = $env:GHPMV_TOKEN
$previousGitHubToken = $env:GITHUB_TOKEN
try {
    $env:GHPMV_TOKEN = $env:TARGET_TOKEN
    Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue
    dotnet run --project src\Ghpmv.Cli -c Release --no-build -- import `
      --org <target-org> `
      --project-number <target-project-number> `
      --in $env:GHPMV_DEMO_SNAPSHOT `
      --repo-mapping "$env:GHPMV_DEMO_SNAPSHOT\repository-mappings.csv" `
      --user-mapping "$env:GHPMV_DEMO_SNAPSHOT\user-mappings.csv" `
      --org-mapping "$env:GHPMV_DEMO_SNAPSHOT\organization-mappings.csv" `
      --enable-browser-automation `
      --browser-profile target
}
finally {
    if ($null -eq $previousGhpmvToken) { Remove-Item Env:GHPMV_TOKEN -ErrorAction SilentlyContinue } else { $env:GHPMV_TOKEN = $previousGhpmvToken }
    if ($null -eq $previousGitHubToken) { Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue } else { $env:GITHUB_TOKEN = $previousGitHubToken }
}
```

data residency の target option と optional mapping は初回 import と同じにする。再 import 成功後、次の browser-assisted verify command を送る。

```powershell
$repairReportPath = Join-Path $env:GHPMV_DEMO_SNAPSHOT 'field-sum-repair-report.json'
$previousGhpmvToken = $env:GHPMV_TOKEN
$previousGitHubToken = $env:GITHUB_TOKEN
try {
    $env:GHPMV_TOKEN = $env:TARGET_TOKEN
    Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue
    dotnet run --project src\Ghpmv.Cli -c Release --no-build -- verify `
      --org <target-org> `
      --project <target-project-number> `
      --in $env:GHPMV_DEMO_SNAPSHOT `
      --repo-mapping "$env:GHPMV_DEMO_SNAPSHOT\repository-mappings.csv" `
      --user-mapping "$env:GHPMV_DEMO_SNAPSHOT\user-mappings.csv" `
      --org-mapping "$env:GHPMV_DEMO_SNAPSHOT\organization-mappings.csv" `
      --enable-browser-automation `
      --browser-profile target `
      --fail-on-warning `
      --report-json $repairReportPath
}
finally {
    if ($null -eq $previousGhpmvToken) { Remove-Item Env:GHPMV_TOKEN -ErrorAction SilentlyContinue } else { $env:GHPMV_TOKEN = $previousGhpmvToken }
    if ($null -eq $previousGitHubToken) { Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue } else { $env:GITHUB_TOKEN = $previousGitHubToken }
}
```

続けて report 自体を検査する。

```powershell
function Stop-FieldSumRepairCheck([string]$Message) {
    Write-Error $Message
    $global:LASTEXITCODE = 1
}
$repairReportPath = Join-Path $env:GHPMV_DEMO_SNAPSHOT 'field-sum-repair-report.json'
if (!(Test-Path -LiteralPath $repairReportPath)) { Stop-FieldSumRepairCheck "Repair report was not created: $repairReportPath"; return }
$repairReport = Get-Content -LiteralPath $repairReportPath -Raw | ConvertFrom-Json
$repairViewCategories = @($repairReport.categories | Where-Object category -eq 'View')
if ($repairViewCategories.Count -ne 1 -or $repairViewCategories[0].status -ne 'Match') {
    Stop-FieldSumRepairCheck "Repaired View category must be Match, actual: $($repairViewCategories.status -join ', ')."
    return
}
$repairViewDifferences = @($repairReport.differences | Where-Object category -eq 'View')
if ($repairViewDifferences.Count -ne 0) { Stop-FieldSumRepairCheck "Repaired View still has differences: $($repairViewDifferences.message -join '; ')"; return }
Write-Output 'GHPMV_FIELD_SUM_REPAIR_MATCH'
$global:LASTEXITCODE = 0
```

target が data residency の場合は repair import / verify にも初回と同じ endpoint option を追加する。`GHPMV_FIELD_SUM_REPAIR_MATCH` と command exit code 0 を確認した場合、browser-assisted verify が `View 1` の Field sum 復元と4 fixture Viewの一致を機械確認済みなので、追加の対話用質問を行わず `browser-e2e field-sum status=repair-match` とする。

`browser-e2e` は `target-render-observed` と `repair-match` の両方へ到達してから Resource inventory の cleanup 同意へ進む。`api-only` は通常の Step 10 完了後に cleanup 同意へ進む。

## Troubleshooting

| エラー / 症状 | 対応 |
|---|---|
| fine-grained PAT preflight / `setup --fixture` で `Resource not accessible by personal access token` | repository endpoint なら **Administration: Read and write** と **All repositories** を確認する。Issue Field endpoint または GraphQL `organization.issueFields` なら Organization **Issue Fields: Read and write** (`issue_fields=write`) に加え、authenticated userがorganization administratorであることが必須。Migrator roleやclassic `admin:org` scopeだけでは代替できない。organization approval、repository creation / PAT policy、SSO も確認する。repository endpointだけが失敗する場合は、同じadministrator accountでrepositoryを先に作成するかclassic PATへ切り替える。 |
| `INSUFFICIENT_SCOPES`, `id`, `read:org` | classic PAT に `read:org` を追加し、必要なら SSO を再承認する。 |
| `The browser session is not signed in to 'github.com'` | 該当 profile で `login` を再実行し、API token と同じユーザーでログインする。 |
| `The browser session is not signed in to '<tenant>.ghe.com'` または host mismatch | エラーを出した side の profile を `login --profile <source-or-target> --base-url https://TENANT.ghe.com` で作り直す。source tenant なら fixture setup に `--api-base-url https://api.TENANT.ghe.com`、export に `--base-url https://api.TENANT.ghe.com` を渡す。target tenant なら fixture setup に `--api-base-url https://api.TENANT.ghe.com`、Import / Verify に `--target-base-url https://api.TENANT.ghe.com` を渡す。browser automation を使う各 command には `--browser-base-url https://TENANT.ghe.com` も渡し、source / target profile と token を混用しない。 |
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
- browser-e2e の field-sum snapshot / initial Match / drift / repair result
- 許容した warning
- resource inventory の各 name / URL / cleanup 状態と snapshot directory

cleanup は workflow 終了時に inventory を示して明示的な同意を質問し、削除を選んだ場合だけ `docs/MANUAL_TEST_PLAN.md` の手順で行う。残す選択では削除しない。PR、commit、push は別途依頼されるまで行わない。

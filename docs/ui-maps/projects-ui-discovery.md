# Projects UI Discovery (D0) — 2026-07-05

実 UI(GHEC, gpm-source/projects/3)での Playwright 操作から得た確定情報。M6/M7 実装の一次資料。

## セレクター確定事項

| 操作 | セレクター | 備考 |
|---|---|---|
| View タブ | `getByRole('tab', { name: <viewName> })` | tablist は `navigation "Select view"` 内 |
| View タブ並べ替え | `[role=tab][href$='/views/{number}']` の source を target の左右端へ drag-and-drop | View number で前方一致名を避ける。overflow 時は両 locator を `ScrollIntoViewIfNeededAsync` してから操作 |
| 新規 View 作成 | `getByRole('tab', { name: 'New view' })` → menu `New view` → `menuitem "Table"/"Board"/"Roadmap"` | 選択と同時に view 作成・遷移(保存不要) |
| View リネーム | タブをダブルクリック → `getByRole('textbox', { name: 'Change view name' })` → fill → Enter | 即時保存 |
| View 設定メニュー | フィルターバーの `button "View"`(exact)| `menu > group "Configuration"` に `menuitem "Group by: <val>" / "Markers: <val>" / "Sort by: <val>" / "Dates: <val>" / "Zoom level: <val>" / "Field sum: <val>" / "Slice by: <val>"`。**ラベルと現在値が name に結合**されるため部分一致(`name: /^Group by:/` 等)で特定する |
| Roadmap 日付フィールド | `menuitem "Dates: ..."` → `dialog "Select date fields"` → group "Start date" / "Target date" の `menuitemradio` | Iteration フィールドは "<name> start" / "<name> end" の 2 radio に展開される |
| View 設定の保存 | `button "Save view"` → **確認 alertdialog** "Save display options for <view>?" → `button "Save"` | 設定変更は「Unsaved changes」status で検出可能。保存は 2 段階 |
| Workflow 一覧 | `/orgs/{org}/projects/{n}/workflows`。サイドバー `list "Default workflows"` 内の link | |
| Workflow 編集 | `button "Edit"`(viewing mode)→ 編集 → `button "Save and turn on workflow"` | |
| Auto-add フィルター | `combobox "Filters"`(編集時)/ `textbox "Filters" [disabled]`(閲覧時) | 閲覧モードでも値は読める(UI-export 可能) |
| Auto-add リポジトリ | `button "When the filter matches a new or updated item : <repo>"` | name にリポジトリ名が結合 |

## 挙動の発見(export/import 設計に影響)

1. **プロジェクト作成時に 6 つの workflow が既定で有効**(Item closed / PR merged / Auto-close issue / Auto-add sub-issues / PR linked / Item added)。import 時は「作成→差分適用」になる
2. **workflow の URL ID は保存前は GUID(揮発性・リロードごとに変化)、保存後は数値 ID に変わる**。GraphQL の `number` とは別物。URL 直接遷移は保存済み workflow のみ信頼できる → 未設定 workflow はサイドバーの link name で辿るのが安全
3. **Auto-add workflow は org にリポジトリが 1 つ以上ないと設定不可**("No repositories found")。import 側で前提チェックが必要
4. Workflow 閲覧モードでも設定値(フィルター文字列・対象リポジトリ・Set value)が DOM に出る → **Edit を押さずに UI-export 可能**
5. View 系の設定変更は SPA 内で「Unsaved changes」になり、明示保存が必要(タブ名変更は例外で即時保存)
6. GraphQL read-backのView内容とWorkflow状態はUI操作後に反映されるが、`views(orderBy:{field:POSITION})`はsaved-tab DOM順と一致しない場合がある。実環境診断(2026-08-19)ではDOMが`Fixture Roadmap → View 1 → Fixture Board`、GraphQL POSITIONが`View 1 → Fixture Roadmap → Fixture Board`のまま60秒超乖離した。tab orderは`navigation "Select view"`内のsaved tab `href` DOM順を正とする

## Table / Roadmap Field sum discovery (2026-08-19)

GitHub.com の一時 user-owned Project で Table / Board / Roadmap を作り、2 つの Number field を追加して確認した。

1. Table と Roadmap は `Group by` が `none` の間は `Field sum` 項目を表示しない。グループ化すると Board と同じ `menuitem "Field sum: Count"` が現れる
2. 3 layout とも子 menu の選択肢は `menuitemcheckbox`、`aria-checked` で状態を表す。選択肢は `Count` と Number field 名で、`Count` は既定で checked だが解除可能
3. Table と Roadmap でも複数 Number field を同時に checked にでき、空集合へ戻せる。3 件以上の選択時、親 menu の summary は `Count, Probe Number 1, 1 more` のように省略されるため、export は summary text を parse せず子 menu の checked entry を全件読む
4. Roadmap では親の View menu に `Truncate titles` / `Show date fields` の checkbox も残る。Field sum / Markers の同期は page 全体ではなく、最後に開いた子 menu へ scope しないと無関係な表示設定を変更する
5. Table / Roadmap とも変更後は `button "Save view"` が表示され、`alertdialog "Save display options for <view>?"` の `button "Save"` で確定する。既存の 2 段階保存フローと同じ
6. existing Project の再 import では GraphQL の View update が grouping / UI-only state を一旦 clear する。save 後の reload は未保存でも dirty 表示を消すため、`Save view` が消えたことだけでは永続化を証明できない。grouping、Slice by、Field sum を reload 後に意味的に再読し、不一致なら bounded retry する
7. grouped Table / Roadmap の visible header content は `[class*='group-header-module__groupHeaderContent']`、Number sum label は `[class*='aggregate-labels-module__Label']`。標準 fixture の Table では `Todo 2 (2) Fixture Number: 3.14 Fixture Number 2: 0` のように描画される。`setup --fixture-field-sum-render-check` は reload 後にこの DOM を読み、Count の `N (N)` と各 `Field: numeric-value` を機械検証する
8. `Truncate titles` / `Show date fields` は親 View menu の direct `menuitemcheckbox` で、状態は `aria-checked` に保持される。2026-08-25 の Project #74 live診断ではclickでmenuが即閉じ、`Unsaved changes`/`Save view`は表示されず、menu再openとreload後にも値がpersistした。さらに片方のRoadmapで変更すると未操作のRoadmapにも同じ値が反映され、2 controlはProject内の全Roadmapで共有されることを確認した。menu textにはcurrent valueを表示しないため、ghpmvは各Roadmapから`aria-checked`を読み、全Roadmapへ同じshared stateを適用する

## Roadmap title/date display discovery (2026-08-25)

GitHub.com の一時 organization-owned Project #74 で `Truncate titles` / `Show date fields` を診断した。

1. 両 control は親 View menu の direct `menuitemcheckbox` で、state は `aria-checked`、disabled=false。menu text に current value は表示されない
2. click 直後に menu は閉じ、`Unsaved changes` / `Save view` は表示されない。menu 再openと同じ BrowserSession 内の reload では変更値を読める
3. 片方の Roadmap で変更すると未操作の Roadmap にも同じ値が反映され、View単位のstateではない
4. 値はProject APIではなくBrowserContextのbrowser storageへ保存される。contextだけを変更してprofileのstorage-state fileへflushしない場合、fresh BrowserSessionでは両値が`false`へ戻る
5. `BrowserSession.SaveStateAsync`でprofileへflush後、別process/fresh BrowserSessionがProject #74のshared state `(false,true)` を取得できた。さらにfresh contextから`(true,false)`へ修復・flushし、別のfresh BrowserSessionで同値を確認した
6. このため両controlはbrowser profile間で移行可能だが、write後のprofile flushとfresh-session read-backをdurability gateとして必須にする
7. 2026-08-26 の target Project #4746 では同じitem titleが固定左列とRoadmap pillの両方に描画された。truncation renderingの判定は`[class*='roadmap-pill-module__SanitizedHtml']`へ限定し、固定左列の常設ellipsisをRoadmap stateとして誤認しない

## Field default UI contract (2026-08-25 live discovery)

GitHub Docs と public schema introspection で Text / Number / Single-select default が browser-only であることを確認し、GitHub.com の一時Project #72で実UIを再確認した。Project `/settings` の`list "Fields"`内にfield nameのlinkがあり、custom fieldは`/settings/fields/{databaseId}`へ遷移する。

- Text: `textbox "Default value"`（placeholder=`Enter default text`）
- Number: `spinbutton "Default value"`
- Single-select: 各optionの`button "Open field actions for <option>"` → 同名menu → `menuitem "Set as default"` / default optionでは`menuitem "Unset as default"`
- Text / Number とSingle-select actionsはいずれもauto-saveで、field pageにSave buttonはない。保存後は`Saved!`が表示される
- default Single-select optionはoption list上で`<option> Default`と表示される

1. export は item values から推測せず、各 supported field の settings control を直接読む。
2. `defaultValue: null` は未取得、`defaultValue: {}` は取得済み clear として区別する。
3. Number は invariant format で読み書きし、`0` と negative を null と区別する。
4. Single-select はoption action menuの`Unset as default`を全optionで探して読み、targetではoption nameのaction menuから`Set as default`を選ぶ。option node IDは保存しない。
5. importはtarget defaultsをitem作成前にclearし、source itemの作成・値適用後にsource defaultsをauto-saveする。既存 item valuesは変更せず、新規itemのみGitHubが自動入力する。
6. auto-save後は2秒待ってsettingsを再度開き、意味的にread-backして不一致をwarningにする。
7. `setup --fixture-field-default-check` はProject viewの`combobox "Start typing to create an item, or type hashtag to select a repository"`へtitleを入力し、`listbox "Discovery menu"`の`option "Create a draft..."`を選ぶ。GraphQL read-backで4 defaultsを確認してitem ID / titleを返す。`addProjectV2DraftIssue` mutationではGitHubがcustom defaultsを適用しないためfunctional checkに使わない。draftはresource inventoryに追加し、明示的なcleanup同意後に`--fixture-field-default-cleanup-item` / `--fixture-field-default-cleanup-title`で削除する。

## フィクスチャー最終状態(gpm-source/projects/3)

- Views:
  - tab order=Fixture Roadmap → View 1 → Fixture Board → Fixture Iteration Board → Fixture Empty Sums → Fixture Roadmap Dates Hidden
  - 1=View 1 (TABLE): filter=`status:Todo`, Group by=Status, Sort by=Fixture Number (asc), Slice by=Fixture Select, Field sum=[Count, Fixture Number, Fixture Number 2], visibleFields=既定 5 + Fixture Text + Fixture Date(Fixture Number はソート由来の仮想列のため visibleFields に入らない — 下記 E2E 知見 8)
  - 2=Fixture Board (BOARD): Column by=Fixture Select, Swimlanes=Status(GraphQL groupByFields に反映), Field sum=`Fixture Number` (Count は uncheck 済み)
  - 3=Fixture Roadmap (ROADMAP): Group by=Status, Field sum=Fixture Number 2, Dates=Fixture Date → Fixture Sprint end, Zoom=Quarter, Markers=[Fixture Date]
  - 4=Fixture Empty Sums (TABLE): Group by=Status, Field sum=[]
  - 5=Fixture Roadmap Dates Hidden (ROADMAP): Group by=Status, Field sum=Fixture Number 2, Truncate titles=on, Show date fields=off
  - 6=Fixture Iteration Board (BOARD): Column by=Fixture Sprint, Sprint 0=1, Sprint 1=3, Sprint 2/3=unlimited
- Workflows 9(GraphQL 可視分): 既定 6 enabled + Auto-add to project (#7: repo=fixture-repo, filter=`is:issue is:open`) + **Auto-add secondary**(repo=fixture-repo, filter=`is:issue label:bug`, enabled)+ **Code changes requested**(保存済み disabled, Set value=In Progress)
- Field defaults: Fixture Text=`既定値 🌏`、Fixture Number=`-7`、Fixture Number 2=`0`、Fixture Select=`Beta`
- fixture-repo: private, Issue #1/#2(gpm-target 側にも同名 repo あり — workflow E2E 用)

## M7 E2E 実走で確定した追加知見(2026-07-05)

1. **Status options を API で上書きすると、既定 workflow の値バインディングが外れる**。外れた状態の "Set value" ボタンの accessible name は **"Set valueundefined"**(GitHub UI の quirk)+ "A value is required" 表示。→ import では全 workflow の Set value 再設定が必須(実装済み)
2. **セレクター regex はプレフィックス一致のみにする**。accessible name は改行を含むことがあり `$` アンカーは不一致を起こす("Set value : "・"When ... : " の値サフィックスはバインディング消失時に消えるため必須にしない)
3. **編集モードで実効差分が無いと Save ボタンは disabled のまま** → クリック待ちでハング。disabled なら Discard で抜ける(SaveWorkflowAsync 実装済み)
4. **リポジトリ picker は入力後に非同期で再フィルター**(デバウンス+fetch)→ option は CountAsync 即時判定でなく WaitForAsync(10s) で待つ
5. View タブの**リネーム用ダブルクリックは新規タブ作成直後に不発になることがある** → textbox 出現を 5s 待ち×3 リトライ
6. **EMU/SAML のセッションは短命**(数時間で失効)。失効時は `/login` リダイレクトではなく **enterprise SSO インタースティシャル**("Single sign-on to <Enterprise>" + Continue)が出る。BrowserSession.GotoAsync は Continue 自動クリックで IdP セッションが生きていれば透過再認証、死んでいれば失敗 → `ghpmv login` 再実行が必要(IdP セッションまで失効すると素の `/login` リダイレクトになる)
7. 並列テスト実行時(browser E2E + integration 同時)は SPA ハイドレーションが遅くなる → Playwright 既定タイムアウトは 30s に設定

## Project collaborators UI export discovery (2026-07-06)

GraphQL has `updateProjectV2Collaborators` but no read field for current project collaborators. The web UI exposes **explicit** collaborators at:

`/orgs/{org}/projects/{number}/settings/access`

Confirmed with `ravel-maurice-uo_sde` temporarily added as a WRITER collaborator to `gpm-source/projects/3`:

```yaml
- heading "Manage access" [level=3]
- checkbox "Select all collaborators. 1 member"
- checkbox "Select ravel-maurice-uo_sde"
- img "ravel-maurice-uo_sde"
- link "Ravel Maurice":
  - /url: /ravel-maurice-uo_sde
- text: ravel-maurice-uo_sde
- 'button "Role: Write"'
- button "Remove"
```

Selectors/parse strategy:

| Data | UI source |
|---|---|
| User collaborator login | `checkbox "Select <login>"` + profile URL `/login` |
| Team collaborator slug | `checkbox "Select <team display name>"` + team URL `/orgs/{org}/teams/{slug}` |
| Role | adjacent `button "Role: Read|Write|Admin"` |

Important limitations:

- This captures only **explicit collaborators** listed under Manage access.
- Inherited access (organization owners, base role, enterprise policies, repository/team inheritance) is not represented as collaborator rows and is intentionally not exported.
- Adding the exporting user as a collaborator may not create a visible row if they already have inherited admin access.

## E2E カバレッジ強化で確定した追加知見(2026-07-06)

1. **Board の横グルーピングは「Group by」ではなく「Swimlanes」メニュー項目**(`menuitem "Swimlanes: <value>"`)。Board のメニューは `Fields / Column by / Swimlanes / Sort by / Field sum / Slice by` の 6 項目で "Group by" は存在しない。GraphQL の `groupByFields` は board では Swimlanes を反映するため、import は board のとき Swimlanes メニューで適用する
2. **Field sum は Board と grouped Table / Roadmap 共通のチェックボックスオーバーレイ**(`menuitemcheckbox`: "Count" + 数値フィールド名)。親 menu の accessible name は "Field sum: Count and Fixture Number" のように値を含むが、3 件以上では `1 more` に省略されるため、export は子 menu を開いて checked entry を全件読む。submenu が存在して全 entry が unchecked なら `fieldSum=[]`、expected control または checkable entry を取得できなければ View UI 未取得 warning とする。Count は uncheck 可能。Table / Roadmap では未 grouping の間は項目自体が無い
3. **UI のリスト値は散文形式**: "A and B" / "A, B, and C"(カンマ区切りとは限らない)→ ParseListValue は `,` と `" and "` の両方で分割する
4. **Fields オーバーレイのエントリーは `option` ロール + aria-checked**(Field sum / Markers の `menuitemcheckbox` とは異なる)→ チェックボックス走査は両ロール対応が必要(ToggleCheckboxesAsync 対応済み)
5. **Roadmap の親 menu には表示オプションが混在**: Truncate titles / Show date fields(表示設定)+ Markers / Field sum の子 menu。子 menu の checkbox 操作は最後に開いた menu へ scope し、親 menu の表示設定を誤操作しない。menuitem テキスト "Markers: <値>" にはマーカーだけが出る
6. **未保存 workflow のページには enable toggle が存在しない**(URL は GUID)。保存済み workflow の URL は数値 ID だが、この ID は GraphQL workflow number とは独立している。export は GraphQL の enabled 値を使い、詳細ページはサイドバーの name 一致 link で開く。toggle の accessible name も workflow 名とは限らないため、import は main detail pane 内の stateful control (`aria-pressed` / `aria-checked` / checkbox) へ fallback する
7. **未保存 disabled workflow は Edit → "Save and turn on workflow"(設定変更なしでも押せる)→ トグル off で「保存済み disabled」にできる**。未保存状態には toggle がないため、設定値が既に一致する enabled workflow も toggle を探さずこの保存経路で有効化する。保存済み disabled workflow は GraphQL の `workflows` に enabled=false で現れ、閲覧モードで設定値も読める(export 可能)。import は未保存の場合にこの save-once 経路を通す(WorkflowUiImporter.ApplyBuiltInAsync / ApplyDisabledAsync)
8. **ソートキーのフィールドは仮想列として表示される**: Fields オーバーレイで aria-checked=true になるが GraphQL `visibleFields` には永続化されない(uncheck→再 check でも変わらない)。import 側は desired 集合にソート列を含めて誤 uncheck を防止する
9. **Duplicate 直後の workflow は編集モードで開く**("Edit" ボタンが無い)→ import は Save ボタンの有無で編集モードを判定してから Edit をクリックする
10. **Playwright 1.61 の wait タイムアウトは `System.TimeoutException`**(`Microsoft.Playwright.TimeoutException` は存在せず、`PlaywrightException` の派生でもない)→ ブラウザーモジュールの catch は `exception is PlaywrightException or TimeoutException` で両方受ける(リトライ・warning 化がタイムアウトでも機能するように修正済み)

## Board column limit UI contract (2026-08-28)

GitHub公式手順では、Board列名の横にあるcontext menu(`aria-label="Column context menu"`のiconを含むbutton)からmenuitem `Set column limit`を開く。`Column limit` inputへ正整数を入力してdialog内の`Save`を押すと直ちに永続化され、View-levelの`Save view`は不要。上限削除はinputを空にして同じ`Save`を押す。

- 上限ありの列はheaderに`<current count> / <limit>`を表示し、current countがlimitを超えるとhighlightされる。上限はsoft limitであり、item追加やautomationを禁止しない。
- 上限なしはinputが空で、snapshotではentryを作らない。Board capture成功時に全列が上限なしなら`boardColumnLimits=[]`、UIを読めなかった場合は`null`として区別する。
- 列identityは`verticalGroupByFields`のfield名とSingle-select option名またはIteration title。source option/iteration node IDは保存しない。
- selectorは`Sel.BoardColumn*`へ集約する。context button、dialog role/name、counter DOMは公開APIではなくGitHub UI依存であるため、変更時はBrowser E2Eで再確認する。

公式仕様: https://docs.github.com/en/issues/planning-and-tracking-with-projects/customizing-views-in-your-project/customizing-the-board-layout#setting-a-limit-on-the-number-of-items-in-a-column

# Browser Automation 詳細設計: Views / Workflows の解析と再現

GraphQL と Playwright を組み合わせた View・Workflow 移行の詳細設計と実装記録。
現行コードは `src/Ghpmv.Core/Import/ProjectViewImporter.cs` と `src/Ghpmv.Core/Browser/`、実 UI の確定事項は [projects-ui-discovery.md](ui-maps/projects-ui-discovery.md) を正とする。全体プランは [PLAN.md](../PLAN.md) を参照。

- 根拠にした一次情報:
  - GitHub Docs「[Managing your views](https://docs.github.com/en/issues/planning-and-tracking-with-projects/customizing-views-in-your-project/managing-your-views)」「[Changing the layout of a view](https://docs.github.com/en/issues/planning-and-tracking-with-projects/customizing-views-in-your-project/changing-the-layout-of-a-view)」
  - GitHub Docs「[Using the built-in automations](https://docs.github.com/en/issues/planning-and-tracking-with-projects/automating-your-project/using-the-built-in-automations)」「[Adding items automatically](https://docs.github.com/en/issues/planning-and-tracking-with-projects/automating-your-project/adding-items-automatically)」
  - GraphQL スキーマ([2026-07-28 の View mutations](https://docs.github.com/en/graphql/overview/changelog/2026#schema-changes-for-2026-07-28) / [2026-07-30 の View configuration](https://docs.github.com/en/graphql/overview/changelog/2026#schema-changes-for-2026-07-30) / `ProjectV2Workflow`)

---

## 0. 全体像: どこまで API で読めて、何を UI でやるのか

**大原則: 読み書きできるものは GraphQL を使う。UI 操作は API に無い読み取り・書き込みだけ。**

### View のプロパティ別ソースマップ

| プロパティ | export(読み) | import(書き) | 備考 |
|---|---|---|---|
| name / layout / number | GraphQL `ProjectV2View.name/layout/number` | **GraphQL** `createProjectV2View` / `updateProjectV2View` | layout enum: `TABLE_LAYOUT` / `BOARD_LAYOUT` / `ROADMAP_LAYOUT` |
| filter 文字列 | GraphQL `ProjectV2View.filter` | **GraphQL** `updateProjectV2View` | repository / user / organization mapping を適用 |
| 表示フィールドと列順 | **GraphQL** `ProjectV2View.configuration.visibleFields` | **GraphQL** `ProjectV2ViewConfigurationInput.visibleFieldIds` | target field ID へ名前で remap |
| group-by(Table)/ swimlane(Board) | GraphQL `groupByFields` | **UI** | |
| Board の列フィールド | GraphQL `verticalGroupByFields` | **UI**("Column by") | |
| sort(複数キー+方向) | GraphQL `sortByFields`(`ProjectV2SortByField.direction`) | **UI** | |
| **Slice by** | ❌ API に無い → **UI で読む** | **UI** | |
| **Field sum** | ❌ API に無い → **UI で読む** | **UI** | Board と grouped Table / Roadmap。Count、複数 Number field、空集合を complete-set 同期 |
| **Roadmap 設定(Dates / Zoom / Markers)** | ❌ API に無い → **UI で読む** | **UI** | |
| タブの並び順 | **UI**(`navigation "Select view"` 内のsaved tab `href`順) | **UI**(タブの drag & drop) | GraphQL `POSITION`は現行UIのsaved-tab順と乖離する場合がある。`ViewSnapshot.tabPosition`はschema v1のnullable additive field |

### Workflow のプロパティ別ソースマップ

| プロパティ | export(読み) | import(書き) |
|---|---|---|
| name / number / enabled | GraphQL `ProjectV2Workflow` | **UI**(enable は保存操作に内包) |
| トリガー条件・対象(issue/PR)・Set する Status 値・フィルター・対象リポジトリ | ❌ API に無い → **UI で読む** | **UI** |

### Field default のソースマップ

| プロパティ | export(読み) | import(書き) | 備考 |
|---|---|---|---|
| Text default | **UI** | **UI** | Unicode と明示 clear を保持 |
| Number default | **UI** | **UI** | zero / negative を invariant number として保持 |
| Custom Single-select default | **UI** | **UI** | source option ID は保存せず option name で target option を選択。built-in Status はworkflow管理のため対象外 |
| Date default | 対象外 | 対象外 | GitHub が default を提供しない |

`FieldSnapshot.defaultValue = null` は API-only snapshot の「未取得」であり target を変更しない。
present object の typed member が null の場合は「取得済み・default なし」として target を clear する。
import はcaptured target defaultsをitem作成前に一旦clearし、source item の作成・値適用がskipなしで完了した後にsource defaultsを設定する。これにより既存targetのdefaultも、後続resumeで作られるitemもsourceの未設定fieldへ混入しない。

### Insights chart の discovery source map (#48)

2026-08-16 の再調査でも REST / GraphQL に chart / insight API は無い。実 UI の確定事項と未解消 blocker は
[insights-ui-discovery.md](ui-maps/insights-ui-discovery.md) を正とする。

| プロパティ | export(読み) | import(書き) | 備考 |
|---|---|---|---|
| custom chart name / order | **UI** | **UI** | default `Burn up` は target built-in のため collection から除外 |
| kind | **UI** | **UI** | X-axis=`Time` なら historical、それ以外は current |
| filter / layout / X-axis / Group by | **UI** | **UI** | field は name + kind で target へ再解決 |
| Y-axis aggregation / Number field | **UI** | **UI** | Count 以外は Number field 必須 |
| historical data points | 対象外 | 対象外 | target item history から生成。設定だけを移行・verify |

つまり完全移行には **export/import の両側で Playwright が必要**(field defaults、group/sort、Slice by、Field sum、Roadmap 設定、Workflow 詳細、Insights chart 設定)。API-only import でも field/options と View の基本構成は作成されるが、captured defaults は適用されない。既存targetのdefaultは通常維持されるものの、Single-select option再構成でdefault option自体が削除される場合は保持できない。

---

## 1. 前提条件と共通基盤

### 1.1 URL 構造(`{base}` = `https://github.com`。移行先が GHEC with data residency の場合は `https://{tenant}.ghe.com`。GHES は非サポート)

```
組織プロジェクト   {base}/orgs/{org}/projects/{number}
ユーザープロジェクト {base}/users/{user}/projects/{number}
特定 View        {projectUrl}/views/{viewNumber}
Workflows 一覧    {projectUrl}/workflows
特定 Workflow     {projectUrl}/workflows/{uiWorkflowId}
Insights           {projectUrl}/insights
Custom chart       {projectUrl}/insights/{uiChartNumber}
設定             {projectUrl}/settings
```

- `{projectUrl}` は owner type に応じて上記の組織またはユーザープロジェクト URL を使う。
- `viewNumber` は GraphQL の `ProjectV2View.number` と一致する。Workflow の URL ID は GraphQL の `ProjectV2Workflow.number` とは独立しているため、Workflow はサイドバーの accessible name で開く。

### 1.2 認証

- `ghpmv login [--profile <name>] [--base-url <url>]`: headful Chromium を開き、ユーザーが手動ログイン(2FA/SSO/passkey 込み)。ログイン完了を検知すると `IBrowserContext.StorageStateAsync()` で状態を保存する。
- 既定の保存先はプラットフォームの ApplicationData 配下にある `ghpmv/browser-state.json`、名前付きプロファイルでは `ghpmv/browser-state.<profile>.json`。任意の場所を使う場合は `--state-path`、既定プロファイルを環境変数で指定する場合は `GHPMV_BROWSER_STATE` を使う。
- 以降の browser-assisted export/import/verify は `--enable-browser-automation --browser-profile <name>` で保存済みプロファイルを選ぶ。セッションが失効した場合は同じ `ghpmv login --profile <name>` を再実行する。
- GHEC with data residency などホストやアカウントが異なる移行では、`ghpmv login --profile source` と `ghpmv login --profile target --base-url https://{tenant}.ghe.com` でセッションを分け、各コマンドの `--browser-profile` で使い分ける。

### 1.3 ロケール

GitHub Web UI は英語のみのため、アクセシブルネームによるセレクターは環境非依存で安定。`Accept-Language` の考慮は不要。

### 1.4 Playwright 共通設定

```csharp
// BrowserSession.cs(共通基盤)
var browser = await playwright.Chromium.LaunchAsync(new()
{
    Headless = !options.Headful,          // --headful で目視デバッグ可能に
    SlowMo = options.SlowMoMs,            // 既定 0、デバッグ時 300
});
var context = await browser.NewContextAsync(new()
{
    StorageStatePath = storageStatePath,
    ViewportSize = new() { Width = 1600, Height = 1000 },  // 狭いと列メニューが折りたたまれる
});
context.SetDefaultTimeout(30_000);
```

- 操作間ウェイト: 連続 UI 操作の間に 300ms(`Task.Delay`)。並列ページは使わない(1 セッション 1 ページ直列)。ToS 配慮とレース回避を兼ねる。
- **失敗時処理**: 回復可能な UI 操作失敗は warning に追加して続行する。UI write は transaction ではないため、View の補完設定や Workflow が途中まで適用された状態で残る場合があり、移行後の browser-assisted `verify` が必須。View は save / reload 後に grouping、Slice by、Field sum を意味的に再読し、不一致なら最大 3 回再試行する。診断ダンプは未実装。その他の SPA race には repository option 待機(10 秒)など対象要素単位の待機・再試行を使う。

### 1.5 セレクターレジストリ

複数フローで再利用するセレクターは `Sel.cs` に集約する。dialog 内の確定ボタンなど、その操作だけで使う one-off selector は実装箇所に残している。D0 Discovery は 2026-07-05 に完了し、role/name を中心とする実測済みセレクターと UI quirk を記録している。

以下は構成を示す簡略化した擬似コードであり、コンパイル可能なコピーではない。実装は `Sel.cs` を正とする。

```text
internal static class Sel
{
    public static ILocator ViewMenuButton(IPage page)
        => page.GetByRole(AriaRole.Button, new() { NameRegex = new("^(Unsaved changes )?View$") }).First;
    public static ILocator NewViewTab(IPage page)
        => page.GetByRole(AriaRole.Tab, new() { Name = "New view" });
    public static ILocator ViewTab(IPage page, string name)
        => page.GetByRole(AriaRole.Tab, new() { NameRegex = new($"^{Regex.Escape(name)}") });
    public static ILocator ViewLayoutButton(IPage page, string layoutName)
        => page.GetByRole(AriaRole.List, new() { Name = "Layout" })
            .GetByRole(AriaRole.Button, new() { Name = layoutName, Exact = true });
    public static ILocator WorkflowLink(IPage page, string name) => /* sidebar link by name */;
    public static ILocator EditWorkflowButton(IPage page) => /* exact "Edit" button */;
    public static ILocator SaveWorkflowButton(IPage page) => /* "Save workflow" or "Save and turn on workflow" */;
}
```

---

## 2. D0: Discovery フェーズ(2026-07-05 完了)

セレクターの最終確定は実 UI でしかできないため、次を実施した。

1. フィクスチャープロジェクトを `GHPMV_TEST_ORG` に作成
2. Playwright codegen と実 UI 操作で次の操作を確認:
   - New view → レイアウト切替 → Fields 変更 → Group by → Sort by → Slice by → Field sum → filter 入力 → Save changes → Rename → Delete
   - Workflows ページ → 各 workflow を開く → Edit → 設定変更 → Save and turn on workflow → Disable
3. accessibility tree と UI quirk を `docs/ui-maps/projects-ui-discovery.md` に記録
4. `Sel.cs` の browser enrichment / Workflow 用エントリを実測値で確定
5. Workflow の設定値を閲覧モードの DOM から読み取れることを確認

**D0 の成果物**: `docs/ui-maps/projects-ui-discovery.md` と `src/Ghpmv.Core/Browser/Sel.cs`。

---

## 3. 解析すべきパターン: View 編

### 3.1 View インベントリ(この組み合わせを全て扱う)

| # | パターン | 設定項目 |
|---|---|---|
| V-1 | Table 基本 | 表示フィールド選択と列順 |
| V-2 | Table + group-by | 任意フィールド 1 つ(Status/Single-select/Iteration など) |
| V-3 | Table + sort | export は複数キーを保持。v1 browser import が適用するのは先頭キーのみ |
| V-4 | Table / Board / Roadmap + Field sum | grouped Table / Roadmap と Board で Count、複数 Number field、空集合を complete-set 同期 |
| V-4a | Group-header Field sum rendering | standard fixture の grouped Table / Roadmap を reload し、visible Count / Number aggregates を DOM assertion |
| V-5 | Board | Column by(Status / 任意 single-select / iteration) |
| V-6 | Board + swimlane | Group by(横帯)との組み合わせ |
| V-7 | Roadmap | Dates(date フィールド対 or iteration)、Zoom(Month/Quarter/Year)、Markers |
| V-8 | 全レイアウト共通 | filter 文字列(そのまま転記。フィールド名は移行済み前提で互換) |
| V-9 | 全レイアウト共通 | Slice by |
| V-10 | View の name / タブ並び順 | browser-assisted export/verifyでDOM `href`順を読み、browser-assisted importで最小D&Dを適用 |

### 3.2 export: UI からの読み取り手順(APIで正しく取得できない4項目)

対象: saved-tab order / Slice by / Field sum / Roadmap設定。GraphQL exportの後、viewごとに1回だけページを開いて補完し、最後にtab stripのDOM順を取得する。

```
手順(view ごと):
1. {project}/views/{viewNumber} へ goto、NetworkIdle 待ち
2. `Sel.ViewMenuButton(page).ClickAsync()` → 開いた menu の accessible name / checked state を取得
3. メニュー項目のラベルから現在値を読む:
   - "Slice by: <field>" → `ViewUiSnapshot.SliceBy`
   - "Field sum: <fields>" の子 menu を開き、checked `menuitemcheckbox` 全件 → `ViewUiSnapshot.FieldSum` (summary は 3 件以上で `1 more` に省略されるため使用しない)
   - Roadmap のみ: "Dates: <...>", "Zoom level: <Month|Quarter|Year>", "Markers: <...>"
4. Esc でメニューを閉じる
5. `navigation "Select view"`内のsaved tab `href`をDOM順に列挙し、View numberへ変換して`tabPosition`を付与する
```

実装メモ: メニュー項目は「設定名 + 現在値」を accessible name に含むため、label prefix で特定する。複数選択項目は overlay の `aria-checked` を読む。

### 3.3 import: GraphQL 作成後の UI 補完シーケンス

`ProjectViewImporter` が View の再利用または作成、name/layout/filter/visible fields を適用した後、View 1 件あたり次を補完する。**各ステップの後に 300ms ウェイト**。

```
EnrichView(spec, targetViewNumber):
 1. {project}/views/{targetViewNumber} へ goto
 2. Column by(Board のみ): ViewOptions → "Column by" → spec.VerticalGroupBy を選択
 3. Group by / Swimlanes: layout に応じた項目で spec.GroupBy を選択
 4. Sort by: View menu → "Sort by" → 先頭キーを選択 → 必要なら方向トグル
 5. Slice by: ViewOptions → "Slice by" → spec.SliceBy
 6. Field sum: Board または grouped Table / Roadmap で ViewOptions → "Field sum" → 子 menu 内だけを対象に spec.FieldSum の complete set へ同期(Count と空集合を含む)
 7. Roadmap のみ: "Dates" → 開始/終了フィールド対 or iteration を選択、"Zoom level"、"Markers" のチェック群
 8. 保存: View menu → "Save view" → alertdialog の "Save"(dialog が出ない UI variant では直接保存)
 9. 全 View 設定の適用後、target のDOM `href`順と snapshot順から最小移動計画を作り、必要なタブだけdrag-and-drop
 10. 検証は後続の `ghpmv verify --enable-browser-automation` または browser E2E で行う
```

Project conflict は API View stage より前に `--on-conflict skip|update|fail` または `--project-number` で解決する。API importer は source view number と target view number の対応を返し、browser importer はその View に未公開設定だけを適用する。

作成順序: browserで取得した`tabPosition`があるsnapshotはその昇順、未取得のsnapshotはview number昇順で作成する。デフォルトで作られる"View 1"はsnapshotの先頭Viewで上書き(rename + 設定)して消費する。API-only importはtab orderを適用できない旨をwarningにし、browser-assisted importはView設定適用後にtargetのDOM順を読み取って修復する。

---

## 4. 解析すべきパターン: Workflow 編

### 4.1 Workflow インベントリ(built-in 全種)

| # | Workflow 名(= UI 表示名 = GraphQL name) | 設定形状(export/import で扱う値) |
|---|---|---|
| W-1 | Item added to project | 対象(issues / pull requests のチェック)+ Set Status = 値 |
| W-2 | Item reopened | 同上 |
| W-3 | Item closed | 対象 + Set Status = 値 ※既定で有効 |
| W-4 | Code changes requested | Set Status = 値(PR 固定) |
| W-5 | Code review approved | Set Status = 値(PR 固定) |
| W-6 | Pull request merged | Set Status = 値(PR 固定)※既定で有効 |
| W-7 | Auto-close issue | Status が 値 になったら close |
| W-8 | Auto-archive items | フィルター文字列(`is:` / `reason:` / `updated:` のサブセット) |
| W-9 | Auto-add to project | 対象リポジトリ + フィルター文字列。**複数インスタンス可**(プラン上限: Free 1 / Pro・Team 5 / GHEC(DR 含む)20) |
| W-10 | Auto-add sub-issues to project | 有効/無効と UI で公開される設定 |

Workflow filter で確認済みの qualifier は `is:` `label:` `reason:` `updated:` `assignee:` `author:` `repo:` `org:` `no:`(negation 可)。`assignee:` / `author:` / `repo:` / `org:` の識別子は user / repository / organization mapping を構造的に適用し、その他の qualifier と構文は保持する。未解決の識別子または曖昧な Auto-add repository は browser-assisted import の最初の mutation 前にエラーとする。

### 4.2 export: Workflow 詳細の UI 読み取り

GraphQL で `workflows { name number enabled }` を取得後、**enabled かどうかに関わらず全件**について:

```
ReadWorkflow(name):
 1. {project}/workflows へ goto し、サイドバーの name 一致 link を開く
 2. 閲覧モードのまま本文の AriaSnapshot を取得し、以下をパース:
    - "When" 節: 対象種別チェック状態(issue / pull request)
    - "Set" / "Filters" 節: Status 値、フィルター文字列、対象リポジトリ名
 3. 読み取れない項目があれば Edit を押して読み、Discard/戻るで離脱(D0 で要否判定)
 4. Auto-add 複製分: workflows 一覧サイドバーに "Auto-add to project" 系が複数並ぶ。
    一覧の全リンク(role=link)を列挙して W-9 型を複数収集(カスタム名も保持)
```

### 4.3 import: Workflow 設定の操作シーケンス

```
ApplyWorkflow(spec):
 1. {project}/workflows へ goto → サイドバー "Default workflows" から spec.Name のリンクをクリック
    (Auto-add の 2 個目以降: 既存 "Auto-add to project" の行のケバブメニュー → "Duplicate" →
     名前入力ダイアログに spec.Name → 作成後にそのページへ)
 2. Sel.EditWorkflowButton.Click()
 3. spec の形状に応じて設定:
    - 対象種別: "issues" / "pull requests" のチェックボックス(D0 で role 確認)
    - Status 値: "Set" 節のドロップダウン → role=option から spec.StatusValue を選択
      ※ 前提: Status option は M3(API)で移行済みのため同名 option が必ず存在する。
        無ければ即エラー(移行順序バグの検出)
    - リポジトリ選択(W-9): リポジトリピッカーに入力して候補選択。
      リポジトリマッピング CSV で変換したターゲット側リポジトリ名を使う
    - フィルター(W-8/W-9): テキストボックスに Fill
 4. 設定を "Save workflow" / "Save and turn on workflow" で保存
    spec.Enabled == false の場合は保存後に toggle off へ戻す
 5. 検証は後続の `ghpmv verify --enable-browser-automation` または browser E2E で行う
```

順序: W-9(Auto-add)は実装上限 20 を preflight で確認し、超過分を warning + skip する。target plan が 20 未満の場合は GitHub UI が Duplicate を拒否した操作失敗を warning として報告する。

---

## 5. スナップショットのデータモデル拡張

`snapshot.json` に追加するセクション(M2 の JSON スキーマに反映):

```jsonc
{
  "views": [{
    "number": 1, "tabPosition": 0, "name": "Backlog", "layout": "TABLE_LAYOUT",
    "filter": "is:issue -status:Done",
    "visibleFields": ["Title", "Assignees", "Status", "Priority"],   // 列順そのまま
    "groupBy": ["Status"], "verticalGroupBy": [], 
    "sortBy": [{ "field": "Priority", "direction": "DESC" }],
    "ui": {                                    // ← UI でしか読めない部分(export 時に Playwright で補完)
      "sliceBy": "Assignees",
      "fieldSum": ["Estimate"],
      "roadmap": { "startField": "Start", "targetField": "End", "zoom": "Quarter", "markers": ["Milestone"] },
      "scrapedAt": "2026-07-05T00:00:00Z"      // 補完に失敗した場合は ui: null + warnings[] に記録
    }
  }],
  "workflows": [{
    "number": 3, "name": "Item closed", "enabled": true,
    "ui": {
      "contentTypes": ["ISSUE", "PULL_REQUEST"],
      "statusValue": "Done",
      "filter": null, "repository": null
    }
  }]
}
```

`ghpmv export` は既定で API のみを使用する。UI-only データも取得する場合だけ `--enable-browser-automation` を指定し、API-only snapshot を import する場合は取得されていない UI-only 項目をスキップして警告する。

---

## 6. 検証ループ

browser importer 自体は各 view / workflow の適用直後に完全な read-back diff を行わない。移行後は `ghpmv verify --enable-browser-automation` が次を比較する:

1. **API で読める項目**: GraphQLで対象viewをnumber一致で取得し、`layout / filter / groupByFields / sortByFields / verticalGroupByFields / visibleFields`をspecと比較
2. **UI でしか読めない項目**: saved-tab DOM順と§3.2 / §4.2のexport用読み取りルーチンを**そのまま再利用**してターゲットを再スクレイプし、`tabPosition`と`spec.ui`を比較
3. 差分は `verify` コマンドと同じレポーター(期待値/実測値/対象)で出力

手動実行する `BrowserRoundTripTests` は View、Workflow、explicit collaborator を一つの共有シナリオで検証し、
`fixture project → export 1回 → 空プロジェクトへ import 1回 → browser設定適用 → export(再) → snapshot diff`
を行う。各機能の deliberate drift はまとめて適用し、一回の追加 verify で検出する。
新しい browser checkpoint は独立した `[Fact]`、target Project、full round trip を追加せず、このシナリオへ統合する。
`BrowserE2eArchitectureTests` は E2E entry point が一つより増えた場合に deterministic suite で失敗する。

---

## 7. フィクスチャープロジェクト仕様(テストデータ)

`GHPMV_TEST_ORG` に作る基準プロジェクト(セットアップスクリプトは可能な限り GraphQL、View/Workflow 部分は初回手動 + 本ツール自身でのブートストラップ):

- フィールド: Status(Todo / In Progress / Done)/ Fixture Text(text)/ Fixture Number・Fixture Number 2(number)/ Fixture Date(date)/ Fixture Select(single-select)/ Fixture Sprint(iteration, 2 週間)/ Fixture Areas(project multi-select)/ Fixture Teams(organization multi-select Issue Field)
- Views(§3.1 の V-1〜V-10 を網羅):
  1. "View 1" — grouped Table, filter, sort, Slice by, Field sum=[Count, Fixture Number, Fixture Number 2]
  2. "Fixture Board" — Board, Column by, swimlane, Field sum=[Fixture Number]
  3. "Fixture Roadmap" — grouped Roadmap, Field sum=[Fixture Number 2], Dates, Zoom, Markers
  4. "Fixture Empty Sums" — grouped Table, Field sum=[]
- Workflows: W-1〜W-8 を非デフォルト Status 値で有効化、W-9 を 2 本(別リポ + 別フィルター)。1 つは disabled のまま設定を持たせる(§4.3 の D0 論点の検証用)
- Items: issue 10 / PR 3 / draft 3(archived 2 を含む)
- Field defaults: Fixture Text=`既定値 🌏`、Fixture Number=`-7`、Fixture Number 2=`0`、Fixture Select=`Beta`

## 8. 既知のリスクと v1 スコープ外

v1 対象外項目の将来対応方針(v1.x / v2)は [PLAN.md §8「スコープとロードマップ」](../PLAN.md#8-スコープとロードマップv1-対象外と将来対応) で一元管理する。本表は判断の実装的背景のみ記載。

| 項目 | 判断 |
|---|---|
| 表示フィールドの列順 | GraphQL `visibleFields` / `visibleFieldIds` で明示的に再現する |
| View タブの並び順(D&D のみ) | 対応済み。LIS を残す最小 D&D 計画を使い、overflow tab は `ScrollIntoViewIfNeededAsync` 後に操作。既に一致する場合は drag しない |
| disabled workflow への設定適用 | 対応済み。設定保存後に toggle off へ戻す |
| memex 内部 API の直接利用 | 既定では不採用。HAR は現時点で成果物として記録していない。UI 操作不能項目が出た場合に調査・取得を検討 |
| UI 変更による破損 | リリース前の手動 browser E2E と `docs/ui-maps/` の実測記録で確認。回復可能な破損は warning + 対象設定の skip。scheduled/nightly CI は未実装 |
| Insights chart | #48 の blocking Discovery は部分完了。read-only selectors と config shape は確定したが、rename/delete/order/save/error と Y-axis field picker は [UI map §12](ui-maps/insights-ui-discovery.md#12-unknowns--blockers) 解消まで実装しない |

`ViewUiExporter.GraphQlPositionMatchesDomOrder`は、APIのPOSITION順とDOM saved-tab順の一致状態をprojectごとに記録する。新規targetの作成順などによって偶然一致する場合があるため、通常exportでは一致をAPI復旧noticeとして扱わない。Browser round-trip E2Eは、DOM順とGraphQL順が異なることを確認済みの非自明source fixtureに限って現時点の乖離をcapability canaryとしてassertする。そのsourceで一致した場合、E2Eを意図的に失敗させ、複数fixtureで再確認したうえでbrowser readをGraphQL readへ置換できるか再評価する。public mutationにtab-order write inputが追加されたかは別途GraphQL schema/changelogで確認する。

## 9. 実装タスク分解と現在の状態

| ID | タスク | 状態 |
|---|---|---|
| B0 | D0 Discovery(§2)、`Sel.cs` 確定 | 完了 |
| B1 | BrowserSession 基盤(起動/ストレージ/ウェイト) | 完了。診断ダンプは未実装 |
| B2 | `ghpmv login` / `ghpmv setup --browsers` | 完了 |
| B3 | View UI-export(§3.2: sliceBy/fieldSum/roadmap 読み取り) | 完了 |
| B4 | View GraphQL import(name/layout/filter/visible fields) + Table UI 補完 | 完了。複数 sort は best effort |
| B5 | View Board / Roadmap UI 補完(V-5〜V-7) | 完了 |
| B6 | Workflow UI-export(§4.2) | 完了 |
| B7 | Workflow import(§4.3)W-1〜W-8 | 完了 |
| B8 | Workflow import W-9(Auto-add 複数 + 上限処理) | 完了。実装上限は 20 |
| B9 | ラウンドトリップ E2E(§6) | テスト実装済み・手動実行。scheduled/nightly CI は未実装 |
| B10 | View tab DOM-order export / verify + browser D&D import | 完了。API-only / 旧snapshotのnullは比較・修復対象外 |

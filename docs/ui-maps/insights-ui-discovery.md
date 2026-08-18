# Projects Insights UI Discovery (#48) — 2026-08-16

GitHub.com の実 UI と公開 API を再調査した、Insights chart 設定移行の一次資料。
この文書は #48 の blocking Discovery 成果物であり、snapshot schema、exporter、importer、`Sel.cs` はまだ変更しない。

## 1. 調査範囲と環境

- API 再評価日: **2026-08-16**
- UI 実測日: **2026-08-16**
- host: `https://github.com`
- browser profile: `%APPDATA%\ghpmv\browser-state.source.json`
- browser login: `SIkebe`
- viewport: `1600x1000`
- 実測対象:
  - user-owned Project #4: item なし、custom field なし
  - user-owned Project #5: Number (`SP`) と Single select (`Priority`, `Phase`) を含む
- organization fixture `gpm-source/projects/3` は 404 で、organization-owned route は今回再実測できなかった。
- GHEC data residency / EMU / GEI target UI は今回未実測。

Project #4 で `New chart` を押したところ、確認なしで `Chart 1` (`/insights/1`) が即時作成された。
remote mutation の追加承認が得られなかったため rename / delete / reorder / save の実操作と cleanup は行っていない。
この副作用を含む未完了事項は [§12](#12-unknowns--blockers) に列挙する。

## 2. 公開 API の再評価

### 2.1 REST

2026-08-16 時点の公式 [Projects REST API](https://docs.github.com/en/rest/projects) は次の 5 category のみを列挙する。

1. [Draft Project items](https://docs.github.com/en/rest/projects/drafts)
2. [Project fields](https://docs.github.com/en/rest/projects/fields)
3. [Project items](https://docs.github.com/en/rest/projects/items)
4. [Projects](https://docs.github.com/en/rest/projects/projects)
5. [Project views](https://docs.github.com/en/rest/projects/views)

chart / insight endpoint は存在しない。Projects / Views の current REST docs は API version
`2026-03-10` を例示しているが、chart response property もない。

### 2.2 GraphQL

公式 [public GraphQL schema](https://docs.github.com/public/fpt/schema.docs.graphql) と
[GraphQL object reference (`ProjectV2`)](https://docs.github.com/en/graphql/reference/objects#projectv2) /
[mutation reference](https://docs.github.com/en/graphql/reference/mutations) を確認した。

さらに `api.github.com/graphql` の introspection を実行した。

| 確認対象 | 結果 |
|---|---|
| `ProjectV2` fields | 30 fields。name に `chart` / `insight` を含む field は 0 |
| `Mutation` fields | 258 mutations。Projects chart / insight mutation は 0 |
| schema type / input / enum | name に `chart` / `insight` を含む Projects 型は 0 |
| `insight` を含む唯一の mutation name | unrelated な `updateEnterpriseMembersCanViewDependencyInsightsSetting` |

**結論:** #48 は引き続き opt-in browser automation が必要。公開 API が追加された場合は API を優先し、
UI 実装を全面または property 単位で置換する。

## 3. URL と chart inventory

```
organization-owned  {base}/orgs/{org}/projects/{number}/insights
user-owned          {base}/users/{user}/projects/{number}/insights
custom chart        {projectInsightsUrl}/{chartNumber}
```

実測した custom chart URL は `/users/SIkebe/projects/4/insights/1`。末尾は UI chart number だが、
公開 API identity ではないため snapshot identity として保存しない。

accessibility tree:

```yaml
- navigation:
  - list:
    - listitem:
      - heading "Default charts" [level=3]
      - list "Default charts":
        - listitem:
          - link "Burn up":
            - /url: /users/SIkebe/projects/4/insights
            - button "Chart options"
    - listitem:
      - heading "Custom charts" [level=3]
      - list "Custom charts":
        - listitem:
          - link "Chart 1":
            - /url: /users/SIkebe/projects/4/insights/1
            - button "Chart options"
- button "New chart"
```

- chart list は `Default charts` と `Custom charts` の named list に分かれる。
- custom chart の表示順は `Custom charts` listitem の DOM 順で読み取れる。
- default `Burn up` は常に root `/insights`。custom chart は `/insights/{number}`。
- `Burn up` の configure pane は `Save` ではなく **`Save to new chart`** を持つ。default chart を上書きする
  persistent config ではないため、snapshot の migratable collection は **custom charts のみ**とする。
- target に既定で存在する `Burn up` は import で作成・rename・delete・verify しない。

## 4. Verified role/name selector candidates for `Browser/Sel.cs`

ここでは selector 方針だけを確定する。`Sel.cs` への追加は実装 phase で行う。

| 対象 | 実測 selector | 状態 / 備考 |
|---|---|---|
| chart groups | `getByRole('list', { name: 'Default charts' })`, `getByRole('list', { name: 'Custom charts' })` | verified |
| chart link | Custom charts list 内 `getByRole('link', { name: chartName, exact: true })` | verified。duplicate name は §12 の blocker |
| chart options | chart link 内 `getByRole('button', { name: 'Chart options', exact: true })` | verified。page 全体では同名が複数ある |
| create | `getByRole('button', { name: 'New chart', exact: true })` | verified |
| current chart heading | `GetByRole(Heading, new() { NameRegex = new($"^{Regex.Escape(name)}"), Level = 2 })` | verified。dynamic name は必ず `Regex.Escape` する。heading name に `Edit chart name Configure` が結合されるため prefix match |
| rename entry | heading 内 `getByRole('button', { name: 'Edit chart name', exact: true })` | verified |
| configure entry | heading 内 `getByRole('button', { name: 'Configure', exact: true })` | verified |
| configure pane | `getByRole('heading', { name: 'Configure chart', level: 2 })` を起点に scope | verified |
| close pane | `getByRole('button', { name: 'Close configuration pane', exact: true })` | verified |
| Layout picker | `getByRole('button', { name: /^Layout / })` | verified。value が name suffix |
| X-axis picker | `getByRole('button', { name: /^X-axis / })` | verified |
| Group by picker | `getByRole('button', { name: /^Group by \\(optional\\) / })` | verified。current chart のみ |
| Y-axis picker | `getByRole('button', { name: /^Y-axis / })` | verified |
| picker menu | expanded button と同じ accessible name の `role=menu` | verified |
| picker option | menu 内 `getByRole('menuitemradio', { name: value, exact: true })` | verified。選択値は `checked` |
| pane discard | `getByRole('button', { name: 'Discard', exact: true })` | verified。clean state では disabled |
| custom chart save | `getByRole('button', { name: 'Save', exact: true })` | verified。clean state では disabled |
| default chart fork-save | `getByRole('button', { name: 'Save to new chart', exact: true })` | verified。migration では使用しない |
| filter region | `getByRole('region', { name: 'Chart filters' })` | verified |
| filter form/input | region 内 `getByRole('form', { name: 'Filter' })` → `getByRole('combobox', { name: 'Filter' })` | verified。DOM の input に aria-label が無くても computed name は `Filter` |
| loading | `getByRole('heading', { name: 'Loading ...', level: 3 })` | verified during create |
| empty data | `getByRole('heading', { name: 'No data available', level: 3 })` | verified |
| rendered chart | `getByRole('region', { name: 'Interactive chart.' })` | verified on populated historical chart |

`Chart options` menu item names、rename textbox、delete confirmation、filter dirty-state の `Save changes`、
Y-axis field picker、error alert は未実測。推測 selector を `Sel.cs` に入れてはならない。

## 5. Configuration shapes

### 5.1 Current chart

新規 custom chart の default:

```yaml
- heading "Configure chart" [level=2]
- text: Layout
- button "Layout Column": Column
- text: X-axis
- button "X-axis Status": Status
- text: Group by (optional)
- button "Group by (optional) None": None
- text: Y-axis
- button "Y-axis Count of items": Count of items
- button "Discard" [disabled]
- button "Save" [disabled]
```

### 5.2 Historical chart

default `Burn up`:

```yaml
- heading "Configure chart" [level=2]
- button "Layout Stacked area": Stacked area
- button "X-axis Time": Time
- button "Y-axis Count of items": Count of items
- button "Discard" [disabled]
- button "Save to new chart" [disabled]
```

current / historical を切り替える独立 control はない。
公式 [About insights for Projects](https://docs.github.com/en/issues/planning-and-tracking-with-projects/viewing-insights-from-your-project/about-insights-for-projects)
の説明どおり、**X-axis が `Time` なら historical、それ以外なら current** と正規化する。

snapshot に `kind` を持たせる場合も、UI import の source of truth は X-axis とし、
`kind=historical && xAxis!=Time` のような矛盾した contract を許可しない。

### 5.3 Layout

current / historical で同じ `menuitemradio`:

- `Bar`
- `Column`
- `Line`
- `Stacked area`
- `Stacked bar`
- `Stacked column`

### 5.4 X-axis and Group by

X-axis menu は section text `Historical` / `Fields` を含むが、選択可能項目は flat な
`menuitemradio`。

Project #5 での X-axis options:

- historical sentinel: `Time`
- built-in: `Assignees`, `Labels`, `Milestone`, `Parent issue`, `Repository`, `Status`
- custom single-select: `Phase`, `Priority`

Number (`SP`) と Text (`Sprint`) は X-axis に出なかった。Group by は current chart のみで、
`None` と groupable fields を列挙する。現在の X-axis field 自身は Group by menu から除外される
(X-axis=`Status` のとき `Status` は Group by に無い)。

### 5.5 Y-axis

current chart で実測した aggregation:

- `Count of items`
- `Sum of a field`
- `Average of a field`
- `Minimum of a field`
- `Maximum of a field`

Number field がない Project #4 では Count 以外が disabled。Number field `SP` がある Project #5 の
historical chart では `Sum of a field` が enabled だった。公式
[Configuring charts](https://docs.github.com/en/issues/planning-and-tracking-with-projects/viewing-insights-from-your-project/configuring-charts)
は aggregation 選択後に下段 field dropdown が出ると説明するが、その dropdown の role/name は今回未実測。

model は `aggregation = count|sum|average|minimum|maximum` と nullable `fieldIdentity` を分ける。
`count` では field は必ず null、その他では Number field を必須にする。

## 6. Workflows

### 6.1 Inventory / export

1. `/insights` を開く。
2. `Custom charts` list の link を DOM 順に列挙する。この index が order。
3. link を開き、heading の visible name を読む。
4. `Chart filters` の Filter combobox value を読む。
5. `Configure` を開く。
6. Layout / X-axis / optional Group by / Y-axis button の accessible name suffix を読む。
7. Y-axis が Count 以外なら下段 Number field picker を読む。selector は再 discovery 後に確定する。
8. pane を `Close configuration pane` で閉じる。

export は default `Burn up` を collection に入れない。chart 1 件の DOM が壊れた場合は chart name と
property を含む warning にして次 chart へ進む。chart link / config pane 自体が読めない場合はその chart
全体を warning + skip とする。

### 6.2 Create

公式 [Creating charts](https://docs.github.com/en/issues/planning-and-tracking-with-projects/viewing-insights-from-your-project/creating-charts)
は `New chart` → optional rename → filter → `Save changes` と案内する。

実測では `New chart` の click だけで:

- `Chart 1` が Custom charts list に追加
- URL が `/insights/1` に遷移
- reload 後も chart が残存

した。したがって create は transaction ではなく、設定失敗時にも空 chart が残る。`New chart` の click 後に
chart number を保存するだけでは、GitHub が作成してから local log write 前に停止する crash window がある。

既存 create mutation と同じ fail-closed lifecycle を使う:

1. click 前に Custom charts の href/number baseline と pending chart operation を
   `project-import-log.json` (または専用 browser-operation log) へ atomic write する。
2. `New chart` を 1 回だけ click する。
3. 遷移先 href と baseline の差分が exactly one なら、その target chart number を pending operation へ
   atomic bind して設定を続ける。
4. response / navigation が ambiguous な場合、再 click しない。resume は current list と記録済み baseline を
   比較して exactly one の new chart だけを adopt する。
5. new chart が 0 または複数なら manual reconciliation error で停止する。

この project-level lifecycle を item/status 用 `import-log.json` に混在させない。

### 6.3 Rename

現 UI は chart heading 内に `button "Edit chart name"` を持つ。
公式 create 手順は name editor へ入力して Return で確定すると説明する。

rename textbox の exact accessible name と Escape/click-away の挙動は未実測。
実装前に textbox role/name、Enter 後の heading/list/link 同期、duplicate name 許可を再確認する。

### 6.4 Configure / save

1. Configure pane を開く。
2. kind に合わせて X-axis を適用 (`Time` または mapped field)。
3. Layout を適用。
4. current のみ Group by を適用。
5. Y-axis aggregation を適用。
6. Count 以外なら mapped Number field を下段 picker で適用。
7. `Save` が enabled になるまで待って click。
8. pane が clean state に戻り `Save` が disabled、または pane が閉じることを確認。
9. filter を Fill し、公式手順の `Save changes` を click。

save 後の exact state transition と filter save selector は未実測。chart-level read-back を必須にし、
設定単位の warning だけで成功扱いにしない。

### 6.5 Delete

chart link 内の `Chart options` button までは verified。menuitem / confirmation dialog は未実測。
create 直後に failure した chart の cleanup と conflict replacement に必要なため、実装前の再 discovery
を blocking とする。

### 6.6 Order

export は Custom charts list の DOM 順で確定できる。reorder control / D&D handle は accessibility tree に
出ておらず、custom chart を 2 件作る remote fixture 操作が承認されなかったため import は未確定。

次のいずれかを実測してから実装する:

1. accessible D&D (`aria-grabbed`, handle role/name)
2. `Chart options` の Move up / Move down
3. create order が list order を決め、後から reorder 不可

3 なら source order で create し、既存 target charts を再利用する conflict mode では order mismatch を
warning にする。

## 7. Field identity / remapping

UI は field name だけを表示するが、GraphQL `ProjectV2FieldCommon` は `name` と `dataType` を返す。
target 解決は node ID をコピーせず、最低限次の logical identity を使う。

```text
FieldIdentity
  name
  dataType
  builtInKind?  // fixed ProjectV2Field kind。Status は特例
```

| UI value | GraphQL identity 例 | 方針 |
|---|---|---|
| `Time` | field ではない historical sentinel | target lookup しない |
| Assignees | `ASSIGNEES` | built-in kind で解決 |
| Labels | `LABELS` | built-in kind で解決 |
| Milestone | `MILESTONE` | built-in kind で解決 |
| Parent issue | `PARENT_ISSUE` | built-in kind で解決 |
| Repository | `REPOSITORY` | built-in kind で解決 |
| Status | `SINGLE_SELECT`, name=`Status` | default field の特例。custom 同名 field と曖昧になり得る |
| custom single-select | `SINGLE_SELECT` + exact name | target field map で解決 |
| Y-axis field | `NUMBER` + exact name | target field map で解決 |

曖昧性:

- 同名・異種 field は `dataType` で分離する。
- 同名・同種 custom field が複数ある場合、name/kind だけでは解決不能。最初の候補を選ばず preflight error。
- custom field named `Status` は default Status と衝突し得る。source/target の default Status identity を
  field inventory から別扱いする。
- `Time` / `None` / `Count of items` は sentinel であり field name として解決しない。
- UI menu は unsupported field を出さない。snapshot に unsupported kind が来た場合は UI picker failure まで
 進めず contract error。

## 8. Migratable configuration versus data

| 対象 | 判断 |
|---|---|
| custom chart name / order | migratable configuration |
| kind | migratable。X-axis `Time` から導出 |
| filter | migratable。既存 filter mapping を適用 |
| layout | migratable |
| X-axis / Group by | migratable。logical field identity で remap |
| Y-axis aggregation / Number field | migratable |
| default `Burn up` | target に built-in で存在。migration collection から除外 |
| period links (`2 weeks`, `1 month`, `3 months`, `Max`) / custom range | viewer state。configure pane に無く、snapshot 対象外 |
| rendered series / historical data points | **non-migratable** |
| chart SVG / canvas / accessible point labels | derived presentation。snapshot 対象外 |
| loading / empty / error UI state | runtime state。snapshot 対象外 |

historical series は target Project の item history から再計算される。source の point/date/value を export せず、
verify でも比較しない。browser-assisted import / verify は「historical chart configuration」を移行・一致
させるだけで、historical data migration 成功と表示してはならない。

## 9. Save, loading, empty, and error states

| state | verified signal |
|---|---|
| clean configure | `Discard` と `Save` / `Save to new chart` が disabled |
| dirty configure | 未実測。enabled transition を実装前に確認 |
| chart loading | `heading "Loading ..." [level=3]` |
| no data | `heading "No data available" [level=3]` + `No results were returned.` |
| rendered | `region "Interactive chart."`, child heading `Chart`, `application` |
| filter dirty/save | 未実測。公式 docs の `Save changes` を live UI で再確認 |
| validation/network error | 未実測。actual alert role/name、retryability、message extraction を再確認 |

wait は loading heading の消失と、rendered / no-data / error のいずれかの出現を条件にする。
`NetworkIdle` 単独や固定 delay を完了条件にしない。

## 10. Automatic Playwright E2E fixture

既存 browser E2E fixture の fields (`Fixture Number`, `Fixture Select`, `Fixture Date`,
`Fixture Sprint`) を再利用し、source に次を用意する。

| chart | kind / settings | coverage |
|---|---|---|
| `Fixture Current` | Column、X=`Status`、Group by=`Fixture Select`、Y=Average(`Fixture Number`)、non-empty filter | current 全 property |
| `Fixture Historical` | Stacked area、X=`Time`、Y=Sum(`Fixture Number`)、別 filter | historical config。points は比較しない |
| `Fixture Current 2` | Bar、X=`Fixture Select`、Group by=None、Y=Count | multiple chart + order + null field |

test flow:

1. source fixture setup は chart が既に一致すれば no-op。残存 partial chart は cleanup/recreate。
2. browser export で custom charts 3 件と order を取得。
3. API + browser import で target Project へ適用。
4. browser-assisted verify で property 単位に比較。
5. target UI を再 export し、normalized config と order を pure comparison。
6. historical rendered points / period は assertion しない。
7. default `Burn up` が duplicate されていないことを assertion。
8. credentials/state がなければ既存方針どおり `Assert.SkipWhen`。
9. target Project/chart は `finally` で削除。`TestContext.Current.CancellationToken` を全操作へ渡す。

追加 implementation tests:

- accessible name suffix parser
- current/historical normalization
- layout / aggregation enum mapping
- field name/kind resolution と ambiguous duplicate rejection
- default Burn up exclusion
- order comparison
- old snapshot の nullable Insights compatibility
- identifier が chart filter だけに現れる場合の mapping template 出力、preflight analysis、transform
  (`repo:`, `assignee:`, `author:`, `org:`)
- historical points が comparison に入らないこと

## 11. Manual GEI + ghpmv E2E checklist

1. source / target browser profile を `ghpmv login --profile source|target --expected-login ...` で作り、
   各 API token owner と login/host が一致することを確認。
2. source fixture に §10 の fields / items / 3 charts を作成。
3. GEI で fixture repository を移行し、Issue / PR number が保持されたことを確認。
4. `ghpmv export --enable-browser-automation --browser-profile source`。
5. repository / organization / user mapping を完成。
6. `ghpmv import --enable-browser-automation --browser-profile target`。
7. `ghpmv verify --enable-browser-automation --browser-profile target`。
8. source / target の Custom charts list で name と order を比較。
9. 各 chart の filter、Layout、X-axis、Group by、Y-axis aggregation/field を Configure pane で比較。
10. target に default `Burn up` が 1 件だけあることを確認。
11. historical chart は設定一致だけを合格条件とし、過去系列が source と一致しないことを
    expected limitation として記録。
12. target の item history から新しい point が生成されることと、ghpmv が historical points を
    移行済みと表示しないことを確認。
13. warning があれば chart name / property / source value / reason が特定できることを確認。
14. snapshot、verify report、source/target URL を保存し、target resource cleanup を実行。

## 12. Unknowns / blockers

実装開始前に解消必須:

1. `Chart options` menuitem と delete confirmation の role/name。
2. rename textbox の role/name、Enter/Escape、duplicate name の挙動。
3. chart reorder control と save semantics。
4. configure dirty-state の enabled transition と save completion signal。
5. filter dirty-state の `Save changes` role/name と save completion signal。
6. Y-axis aggregation 選択後の Number field picker role/name。
7. validation / network / permission error の alert role/name と recoverability。
8. organization-owned / GHEC data residency UI が user-owned GitHub.com と同じ accessibility contract か。
9. custom field の同名・同種 duplicate と default Status collision。
10. create/resume 時に UI chart number を安定して再取得する方法。

今回の live blocker:

- source browser profile 自体は有効で、`SIkebe` として GitHub.com に sign-in 済み。
- organization fixture は 404。
- remote mutation の追加承認が無く、rename / delete / reorder / save の live operation は未実施。
- discovery 中に作られた user Project #4 の `Chart 1` は cleanup 未実施。

残りの live discovery に必要なもの:

- disposable source organization Project への admin access
- 同じ login の `source` browser profile
- create / rename / delete / reorder / save と cleanup の明示承認
- GHEC/GEI manual E2E には、target organization admin の API token と同じ login/host の
  `target` browser profile

## 13. Recommended implementation decomposition

Discovery blockers を解消後、次の順で分ける。

1. **Contract / pure logic**: nullable custom chart collection (`CurrentSchemaVersion` は上げない)、
   normalization、field identity、comparison、backward compatibility。historical points は model に入れない。
2. **Selectors + read-only exporter**: `Sel.cs`、custom list/order、filter/config reader、default exclusion。
3. **Importer lifecycle**: create → target chart ID persist → rename → configure → filter → read-back。
4. **Delete / conflict / resume / order**: verified menu/D&D selectors 後に実装。
5. **Verifier**: exporter を再利用し property-level diff。historical points は明示的に対象外。
6. **Fixture + E2E + manual plan**: §10 / §11 を実装し、GitHub.com と GHEC/GEI で確認。

API-only path は Insights collection を無視して従来動作を維持し、browser automation が無い場合は
category-level warning / `NotVerified` とする。machine-readable stdout の既存行は変更しない。

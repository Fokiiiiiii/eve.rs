# Marshal 監査指摘の修正記録

## 判定

監査報告の4件をすべてコードで確認し、すべて実在する不具合として修正した。誤検知として見送った指摘はない。

V24.01 全体の `blue.marshal.Load` / `Save` 呼び出しを検索し、42箇所を確認した。呼び出しは Macho packet (`carbon/common/script/net/machobase.py:18-24`)、packet 内 action (`eve/client/script/remote/michelle.py:494`)、cache / pickle (`carbon/common/script/net/objectCaching.py:642-700`)、DB rowset cache (`carbon/common/script/sys/crowset.py:193-260`)、settings (`carbonui/util/settings.py:94,209`)、Dogma state (`eve/common/script/dogma/baseDogmaLocation.py:1835`) にまたがる。同じ marshal wire 契約が client 全体で使われていることを確認した。eve.rs Host は `HostServer.cs:730` で `MarshalCodec.DecodeInbound` を行い、decode 成功後に dispatcher へ渡す。比較対象の EveJS は `/home/user/evejs-261028.1/server/src/network/tcp/utils/marshal.js`。

## 修正内容

### P1 — `TY_PICKLER` の終端

EveJS encoder は `marshal.js:510-523` で opcode の後に pickle bytes を長さなしで出力し、decoder は `marshal.js:1590-1630,1937-1943` で pickle opcode を走査して `STOP` まで読む。従来の .NET / Rust / Python encoder・decoder は長さ prefix を使い、この wire を誤読していた。

各実装に protocol 0–2 pickle opcode scanner を追加し、`STOP` までを値とする。encode は長さを挿入せず、`STOP` の後に余分な bytes がある pickle は EveJS 同様エラーにする。`raw:cpicked-stop-preserves-following-value` fixture は BINUNICODE 内に `0x2e` を含む pickle と後続 marshal 値を組み合わせ、値境界を記録する。

### P1 — 負の `TY_VARINT`

EveJS の `marshal.js:1685-1709` は little-endian two’s-complement として decode する。従来の実装は符号を無視し、例えば `2f 01 ff` を `-1` ではなく `255` としていた。

.NET は `BigInteger`、Rust は signed byte representation、Python は signed `int.from_bytes` を使うようにした。正の値は sign bit が立つ場合に `0x00` を補い、負の値は最小幅の two’s-complement を保持する。`-1`, `-128`, `-129`, `-2^53`, `-2^63`, `+2^63` の raw fixture と回帰テストを追加・更新した。

### P2 — payload と save table の境界

EveJS の read helper は `marshal.js:1444-1512` で全 read を `valueEnd` に制限する。header も `marshal.js:1407-1413` で trailer の index を事前検証し、ObjectEx は `marshal.js:1878-1897` で list / dict の両 terminator を要求する。従来の実装は payload の切断を受理したり、save-table byte を payload として消費したり、ObjectEx terminator の欠落を受理していた。

3実装とも opcode・固定幅値・長さ付き領域・collection count・pickle・ObjectEx terminator を payload 上限内に制限し、save table の index を事前検証するようにした。不足 bytes、境界越え、terminator 欠落は decode error にする。既存の切断値成功 fixture を拒否期待に直し、save table 越境と ObjectEx 欠落の raw case を追加した。

### P2 — 保存参照

EveJS は save table map を `marshal.js:1407-1413` で検証し、list / dict を子値より先に公開する (`1828-1852`)。Object は args より前、ObjectEx は header 後に公開し、無効な `TY_REFERENCE` は `1945-1960` で拒否する。従来の実装は未設定 slot を `None` に置き換え、decode 中の list/dict/object の自己参照を失っていた。

各実装で「未設定 slot」と実際の `None` を区別し、無効・未公開参照を拒否する。list / dict / object / ObjectEx は EveJS の登録タイミングで公開して identity を保ち、self-referencing tuple は EveJS 同様拒否する。saved token、list alias、自己参照 container、無効 index の回帰ケースを追加した。Rust は `SavedGraph` が slot を所有し、参照側は weak pointer を持つため、自己参照 graph を保持しつつ強参照 cycle による leak を避ける。

## fixture と実装範囲

`oracle/fixtures/marshal/cases.json` は 543 件から 556 件へ更新した。追加した13件は監査対象の raw regression vector。既存の CPickle fuzz vectors は EveJS が invalid pickle を拒否する結果に更新し、古い誤った signed-varint・境界・参照期待値も修正した。無関係な Unicode / packed-row の差分は含めていない。

この変更は marshal codec と Host の受信 decode 境界に限られる。codec に gameplay state mutation、DB persistence、通知処理はないため、gameplay 遷移自体の変更はない。

## 検証

確認済みのソース比較: V24.01 client callsites、Host ingress、EveJS `261028.1` の該当 encoder / decoder。新しい回帰テストは修正前に失敗することを確認してから実装した。

実行済み:

- .NET `Eve.Marshal.Tests` Release: 1,157 passed、0 failed、0 skipped。
- Rust の `eve-marshal` / `eve-oracle` 全 package tests: 28 passed、0 failed、0 ignored。うち `marshal_audit_regressions` は7件、`marshal_golden` は1件。切断文字列の robustness test は EveJS の `valueEnd` 検査を根拠に、両 decode mode で `UnexpectedEnd` を検証する。
- Rust `cargo fmt --all -- --check`、`cargo check -p eve-marshal -p eve-oracle`、Release check、`cargo clippy -p eve-marshal -p eve-oracle --all-targets -- -D warnings`: passed。
- Python `test_marshal_golden.py`: passed。`pytest-timeout` をテスト用 `/tmp` venv に導入後に再実行し、timeout 設定警告はなかった。
- .NET `Eve.Host.Tests` とその依存を含む Release build: 0 warnings、0 errors。追加した Host ingress の実 wire test 2件は `eve_p3` で2 passed。`eve_p10` では同2件と下記の raffle 2件を合わせて4 passed。
- 最終 marshal class slices (`eve_p3`, `eve_p10`): 18 test projects を両 DB で実行し、18/18 project filters が exit 0。各 DB の Host filter は793 passed / 21 skipped、Dogma は56 passed / 1 skipped、Marshal は1,157 passed。その他の15 project filters も失敗なし。Skip は Host の21件 (`EVE_ABYSSAL_SIM_BINARY` と `EVE_ABYSSAL_SIM_SDE` 未設定) と Dogma の1件 (`EVE_DOGMA_BENCH=1` が必要な opt-in benchmark) で、pass と数えていない。
- 先行 run では既定 Crimewatch schema に前回テストが残した遠い将来 timer があり、raffle の2件が Host 起動中 `Task.Delay` 上限で失敗した。原因は `CrimewatchHostWireTests.Crimewatch_bound_get_client_states_returns_a_persisted_active_receiver_timer` が既定 schema に残した row。専用 `EVE_CRIMEWATCH_SCHEMA` を指定した最終 slices は両 DB とも全 project filters が成功し、追加した Host ingress tests も p3 で2/2、p10 で4/4 passed。test や製品 assertion は弱めていない。
- 後から merge した character selection / ship-ammo code の関連 test classes も、最新作業 tree で両 DB に実行した。各 DB で Host 69 passed / 1 skipped、CharacterSelect 38、Dogma 9、Ships 15、Space 12、Inventory 26 tests passed。Host の skip は `Confirmed_ship_clone_jump_moves_the_capsule_settles_state_and_notifies_after_space_entry` の sim 依存テスト1件。
- character selection の headless live regression は `tools/dev/run-eve-live.sh` で p3 / p10 各6 passed (recorded walk、named arguments、4 race skill cases)。最初の起動は ESI market seed が60秒を超えたため test 前に中断し、selection flow に無関係な market seed を `EVE_MARKET_SEED=skip` にして再実行した。テストは利用可能な `/tmp/eve-cx3-python` environment から起動した。

main CI run 1650 (`bec083d`) は全 job と Required CI gate が success。これは marshal 変更を含まない main の検証結果で、今回の変更を push した後の CI は未実行。

未確認: 実 V24.01 graphical client による接続・Macho packet の再生、EveJS を起動した scenario 実行、および今回の変更を push した後の GitHub Actions CI。

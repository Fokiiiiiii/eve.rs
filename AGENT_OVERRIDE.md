# Implementation Completion Rule

新機能は、RPC・route・stubの追加だけで完成扱いにしないこと。

実装時は必ず以下を確認する。

- client callsiteと実際の利用フロー
- wire引数・戻り値shape
- authoritative stateへの接続
- read / mutationの実処理
- persistence
- 必要なnotification
- session / state transition
- 関連Inventory・Wallet・Dogma等との副作用
- 正常系・失敗系・再接続後の挙動

既存backendがある場合は新しい仮実装を作らず、それへ接続する。

固定値、空配列、PyNone、仮データを返す実装は明示的に未完成として扱う。

完了前にEveJSとV24.01 client logicを比較し、実クライアントから機能を最後まで操作できるテストを追加する。

「routeが存在する」「unit testが通る」だけを完成条件にしてはならない。

# Crate fitting の保存経路と再請求修正

更新日: 2026-10-13。canonical mainで親が実装・検証。

## 再現した不具合

- PersistenceCrateServiceBackend.ClaimFittingAsyncはLegacyGameStoreのsavedFittingsへ書込み、charFittingMgrはPgSavedFittingStoreを読む。回帰テストで、claimがIDを返しても通常GetFittingsが空になることを再現した。
- 同じpending fittingを並列claimするとbackendが2回呼ばれることをTaskCompletionSourceを使うテストで再現した。

## 修正

- Hostは通常fitting RPCと同じsavedFittings instanceをcrate backendへ渡す。
- 元catalogは読取りに使い、獲得したfitはPgSavedFittingStore.SaveManyAsyncへ保存する。source catalogのowner・next ID・原本は変更しない。
- 既存のSDE type/slot/name制約と500件limitを維持し、非正quantityは拒否する。backend未接続では架空IDを返さない。
- claimは短いmetadata lockでpending countを予約してからsaveする。DB awaitはlockの外。save成功だけ消費を確定し、null・exceptionで予約を戻す。別claim完了後のfailure restoreと、新しいpending登録の競合も同じ状態ownerで扱う。

## 検証

- Core 691 tests PASS、0 fail/skip。crateは単一pendingの二重save拒否、null/exception再試行、並行完了後の復元を含む。
- CrateServicePersistenceTests 2 tests PASS、0 fail/skip。実PGへsave、charFittingMgr.GetFittingsへ即反映、store再生成後の保持、再claim拒否、未知fit、負quantity、500件limit、catalog原本不変を検証。

## 未実装・未確認

- pending skill/fitting entitlementは現在service内のmemory。Host再起動をまたぐ未請求reward復元は未実装。
- DB commit結果が不明な切断を含むexactly-once claim ledgerは未実装。今回のretry testはcommit前のnull/exceptionを対象とする。
- 旧LegacyGameStoreへ過去に保存されたpersonal fittingsの移行は未実装。ユーザーデータを自動転記していない。
- GUI受入、全RPCの実行互換、restart時のpending claimsは完了と判定していない。

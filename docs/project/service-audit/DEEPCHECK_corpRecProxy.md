# Deep check: corpRecProxy

## Whole-client flow

The ProxySvc facade is eve/client/script/ui/services/corporation/base_corporation.py:638-660. The direct UI consumers are corp_ui_recruitment.py:47,176; recruitment/corpRecruitmentAdWindow.py:39; recruitment/corpRecruitmentContainer.py:218,653,657,917,1240; and recruitment/recruitmentEntry.py:444,458. Ad rows are used as recruitment advert records keyed by corporationID/adID and displayed by the entry/container views; GetRecruiters supplies a separate recruiter ID list.

| Method | Client request and reply |
|---|---|
| GetRecruitmentAdsForCorporation | no args; bco_recruitment.py:44-56 consumes an advert list and builds a Rowset. |
| GetRecruitmentAdsByCorpID(corpID) | one positional corporation ID; recruitmentContainer.py:917 consumes an advert list for that corporation. |
| GetRecruiters(adID) | one positional advert ID; callers at corp_ui_recruitment.py:176, corpRecruitmentAdWindow.py:39, container.py:218 and recruitmentEntry.py:444 consume a recruiter ID list. |
| GetRecruitmentAd(corpID, adID) | two positional IDs; base_corporation.py:659-660 and :1061 return the advert record to the corporation service. |
| SearchCorpAds | eleven positional filter values; the local wrapper is called with keywords at recruitmentContainer.py:1240, then base_corporation.py:656-657 forwards them positionally; returns matching advert rows. |
| CreateRecruitmentAd | nine positional values; base_corporation.py:638-639 forwards the client form fields and consumes the returned advert ID. |
| UpdateRecruitmentAd | eight positional values and keyword minSP and otherMask; base_corporation.py:641-642 forwards the result, which is ignored by recruitmentContainer.py:653. |
| DeleteRecruitmentAd(adID) | one positional ID; base_corporation.py:644-645 and recruitmentEntry.py:456-459 ignore the reply. |

The CSV has eight distinct methods, with repeated source rows for GetRecruitmentAdsForCorporation. The full set is the eight methods listed above. All method calls use positional arguments except the two UpdateRecruitmentAd keywords described above; only transport machoVersion is stripped by the dispatcher.

## Host behavior and concrete wire gap

CorpRecruitmentProxyService is annotated as corpRecProxy in CorpRegistryService.cs:1946 and is registered in production by CorporationRpc.cs:70. Its reads use CorporationStore and PgCharacterStore, return empty lists for missing read-side owners, None for a missing specific advert, and rowsets built by CorporationWire.RecruitmentAdRow. Create persists an advert and returns its ID; update/delete persist changes and return None. A successful mutation schedules the corporation recruitment change notification after the RPC response.

Create failures raise CORPORATION_ACCESS_DENIED for missing actor state, CORPORATION_INVALID_RECRUITMENT_DURATION for invalid duration, CORPORATION_BULK_LIMIT_EXCEEDED for more than 100 recruiters, and CORPORATION_INVALID_RECRUITER for invalid or duplicate recruiter IDs. Update/delete require a character and raise CORPORATION_ACCESS_DENIED when absent. Updating a missing ad returns None; invalid added duration raises CORPORATION_INVALID_RECRUITMENT_DURATION.

There was a client-shape bug: UpdateRecruitmentAd receives minSP and otherMask as keyword fields at base_corporation.py:641-642, but the Host handler previously read only call.Arg(8) and call.Arg(9). PacketDispatcher passes keyword fields separately, and the V24 call does not supply positional slots 8 and 9, so edits could overwrite those filters with defaults. The Host now reads the named keywords first and retains positional fallback. Current EveJS 261028.1 reads positional args at server/src/services/corporation/corpRecProxyService.js:311-331, while BaseService.callMethod at server/src/services/baseService.js:69-101 passes kwargs as a distinct handler argument. The V24 wire shape takes precedence, and the deviation is recorded in DEVIATIONS.md.

## Tests and next required check

CorpRecruitmentProxyHostWireTests.cs (Recruitment_ads_read_update_and_delete_through_registered_host, lines 17-129) covers the create, update, delete, recruiter read, advert read, corporation list, session corporation list, and search methods through registered Host dispatch. It verifies the `util.Row` columns and line values, persisted updates to minSP and otherMask sent as keywords, and post-response recruitment-change notifications for each successful mutation. Next-check status: the only check listed in this section, the empty GetRecruitmentAdsForCorporation route check, is already done at CorporationClientRpcCoverageWireTests.cs:61 inside Corporation_client_reads_route_through_host_with_transport_kwargs (lines 10-78). The minSP and otherMask keyword persistence is done in Recruitment_ads_read_update_and_delete_through_registered_host (CorpRecruitmentProxyHostWireTests.cs:105-106 sent, :110-111 asserted). No other next check is listed here, so none remains open from this section. CorporationProtocolTests.cs:3379-3508 (Corp_recruitment_ads_persist_filter_notify_and_replay_by_call_id and Corporation_recruitment_ad_enumeration_refuses_more_than_5000_rows) retains store/service regression coverage.

Local verification on database eve_p3: Eve.Host.Tests build succeeded with 0 warnings and 0 errors; CorpRecruitmentProxyHostWireTests passed 1/1; CorporationProtocolTests passed 51/51; the existing registered-route recruitment read passed 1/1. All had 0 skipped tests. Push-triggered CI run 1357 completed successfully. The graphical V24 client was not used.

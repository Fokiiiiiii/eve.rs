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

There is a client-shape bug: UpdateRecruitmentAd receives minSP and otherMask as keyword fields at base_corporation.py:642, but the current Host handler reads call.Arg(8) and call.Arg(9) in CorpRegistryService.cs:2134-2135. PacketDispatcher passes keyword fields separately, and the V24 call does not supply positional slots 8 and 9. Thus edits can overwrite those filters with defaults. EveJS v0.12.9 also reads positional args at server/src/services/corporation/corpRecProxyService.js:300-320, but BaseService.callMethod at server/src/services/baseService.js:69-101 passes kwargs as a distinct handler argument; that reference behavior does not override the V24 wire shape.

## Tests and next required check

CorporationClientRpcCoverageWireTests.cs:61 only checks the empty GetRecruitmentAdsForCorporation read over the registered Host route. The other methods have service/store tests in CorporationProtocolTests.cs:2981-3059, but no equivalent real Host wire test source was found. The client-shape mismatch needs a focused Host test that sends the two keyword values and verifies persisted values on a follow-up read. That success path uses the corporation database owner and was not exercised here.

No PostgreSQL database or graphical V24 client was used in this pass. No code or test was changed.

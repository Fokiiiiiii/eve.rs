# Dogma: how attributes are evaluated

From the client's implementation (*dogma/*, *eve/common/script/dogma/*); the
static tables it evaluates come from the SDE (`typeDogma`, `dogmaAttributes`,
`dogmaEffects`).

## Modifier operators

| Value | Operator | Value | Operator |
|---|---|---|---|
| -1 | PreAssignment | 4 | PostMul |
| 0 | PreMul | 5 | PostDiv |
| 1 | PreDiv | 6 | PostPercent (`v * (100 + p) / 100`) |
| 2 | ModAdd | 7 | PostAssignment |
| 3 | ModSub | 8 | PostPercentUnNerfed |
| 9 | SpecialSkillRelationship | | |

Application order: `[-1, 0, 1, 2, 3, 4, 5, 6, 8, 7]`.

## Modifier functions and domains

- Functions: `ItemModifier`, `LocationModifier`, `LocationGroupModifier`,
  `LocationRequiredSkillModifier`, `OwnerRequiredSkillModifier`.
- Domains: `itemID` (self), `shipID`, `charID`, `targetID`, `otherID`,
  `structureID`.
- A `modifierInfo` entry: `{func, domain, operator, modifiedAttributeID,
  modifyingAttributeID, skillTypeID | groupID}`.
- Effect categories: 0 passive, 1 activation, 2 target, 3 area, 4 online,
  5 overload, 6 dungeon, 7 system.

## Stacking penalty

An attribute with `stackable == false` is evaluated with the stacking penalty.

- Penalised operators: PreMul, PostMul, PostPercent, PreDiv, PostDiv.
- Exempt sources: skills, implants, ships, charges and subsystems (by
  category), and any modifier marked unpenalised.
- Take each modifier's factor `op(1, v)`; sort those below 1 ascending and
  those above 1 descending (two chains); index each chain from 0.
- The k-th factor `f` becomes `(f - 1) / exp((k / 2.67)^2) + 1`; the results
  are multiplied. At most 8 factors count (`MAX_NURF_SEQUENCE_LENGTH`).

The decompiled loop has an odd `if len(nurfedMods) <= 0` branch that is a
decompiler artefact; verify an implementation against known in-game values,
not against that branch.

## Destiny constants the client confirms

Only `DESTINY_MODE_ORBIT = 4` (*eve/client/script/remote/michelle.py*) is
present as a number; the ball wire layout and the other mode and flag values
live in the native module and are not in the client source, so the values in
`crates/eve-destiny` come from EVE.js and its fixtures, not from the client.

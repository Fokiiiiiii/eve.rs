use super::*;

const SECOND: i64 = TICKS_PER_SECOND;

/// An Ibis's points (220 shield, 137.5 armor, 201.25 hull; the shield takes 860 s to recharge) with resistances made up so that each kind of
/// damage does something different to each layer (the real ones are the host's, worked out by dogma).
fn frigate() -> Vitals {
    Vitals {
        shield_capacity: 220.0,
        armor_hp: 137.5,
        structure_hp: 201.25,
        resonances: Resonances {
            shield: [0.0, 0.8, 0.6, 0.5],
            armor: [0.5, 0.35, 0.25, 0.1],
            structure: [0.33, 0.33, 0.33, 0.33],
        },
        armor_pinned: false,
    }
}

fn health() -> Health {
    Health::new(frigate(), 860_000.0, Condition::FULL, 0)
}

#[test]
fn a_whole_ship_is_told_whole() {
    let state = health().state_at(0);
    assert_eq!(state, DamageState { shield: 1.0, shield_tau_ms: 172_000.0, armor: 1.0, structure: 1.0 });
}

#[test]
fn a_thing_with_no_points_is_told_whole_with_the_recharge_of_a_station() {
    let bare = Health::new(Vitals::default(), 860_000.0, Condition::FULL, 0);
    assert_eq!(bare.state_at(0), DamageState::whole(UNHURTABLE_TAU_MS));
    let mut bare = bare;
    assert!(bare.apply(0, [10.0; 4]).is_none());
}

#[test]
fn a_hit_is_taken_by_the_shield_through_its_resonances() {
    let mut health = health();
    // 100 thermal, 100 kinetic: the shield lets through 0.8 and 0.6 of them
    let applied = health.apply(0, [0.0, 100.0, 100.0, 0.0]).unwrap();
    assert_eq!(applied.before.shield, 220.0);
    assert!((applied.after.shield - (220.0 - 140.0)).abs() < 1e-9);
    assert_eq!(applied.after.armor, 137.5);
    assert_eq!(applied.after.structure, 201.25);
    assert!(!applied.destroyed);
    assert_eq!(applied.taken.shield, 140.0);
    assert!((health.state_at(0).shield - 80.0 / 220.0).abs() < 1e-6);
}

#[test]
fn a_damage_over_time_tick_goes_through_the_layers_whole_while_a_hit_is_resisted() {
    // 100 thermal: the shield lets 0.8 of a hit through, but a damage-over-time tick ignores resistances (the wiki) and takes all of it
    let resisted = health().apply(0, [0.0, 100.0, 0.0, 0.0]).unwrap();
    let whole = health().apply_unresisted(0, [0.0, 100.0, 0.0, 0.0]).unwrap();
    assert!((resisted.taken.shield - 80.0).abs() < 1e-9);
    assert!((whole.taken.shield - 100.0).abs() < 1e-9);
}

#[test]
fn a_resolved_applied_damage_depletes_layers_without_a_damage_type() {
    let mut health = health();
    let applied = health.apply_resolved_amount(0, 400.0).unwrap();

    assert_eq!(applied.taken, Layers { shield: 220.0, armor: 137.5, structure: 42.5 });
    assert_eq!(applied.after, Layers { shield: 0.0, armor: 0.0, structure: 158.75 });
    assert_eq!(applied.armor_damage, (0.0, 137.5));
    assert!(!applied.destroyed);
    assert!(health.apply_resolved_amount(SECOND, f64::NAN).is_none());
}

#[test]
fn what_the_shield_cannot_take_goes_on_to_the_armor_in_the_same_proportions() {
    let mut health = health();
    // 500 explosive: the shield holds 220 points and takes 0.5 of each point, so it soaks 440 of them; 60 explosive are left for the armor
    let applied = health.apply(0, [0.0, 0.0, 0.0, 500.0]).unwrap();
    assert_eq!(applied.after.shield, 0.0);
    assert!((applied.taken.shield - 220.0).abs() < 1e-9);
    assert!((applied.taken.armor - 60.0 * 0.1).abs() < 1e-9);
    assert!((applied.after.armor - (137.5 - 6.0)).abs() < 1e-9);
    assert_eq!(applied.after.structure, 201.25);
    assert_eq!(health.condition_at(0).shield_charge, 0.0);
}

#[test]
fn a_mixed_hit_uses_up_the_shield_and_takes_the_rest_of_each_kind_from_the_armor() {
    let mut health = health();
    let applied = health.apply(0, [200.0, 200.0, 200.0, 200.0]).unwrap();
    // effective against the shield: 0 + 160 + 120 + 100 = 380 points, the shield has 220: 220/380 of the hit is used
    assert_eq!(applied.after.shield, 0.0);
    let left = 1.0 - 220.0 / 380.0;
    let expected: f64 = [0.5, 0.35, 0.25, 0.1].iter().map(|r| 200.0 * left * r).sum();
    assert!((applied.taken.armor - expected).abs() < 1e-6, "{} {}", applied.taken.armor, expected);
    assert!(applied.taken.structure == 0.0);
}

#[test]
fn a_hit_that_goes_through_everything_destroys_the_ship() {
    let mut health = health();
    let applied = health.apply(0, [10_000.0; 4]).unwrap();
    assert!(applied.destroyed);
    assert_eq!(applied.after, Layers { shield: 0.0, armor: 0.0, structure: 0.0 });
    assert_eq!(health.state_at(0).structure, 0.0);
}

#[test]
fn a_hit_that_does_nothing_does_not_change_the_ship() {
    let mut health = health();
    let applied = health.apply(0, [0.0; 4]).unwrap();
    assert!(!applied.changed && !applied.destroyed);
    assert!(applied.attribute_changes().is_empty());
    // damage below nothing is nothing
    let applied = health.apply(0, [-5.0, -1.0, -3.0, -9.0]).unwrap();
    assert!(!applied.changed);
}

#[test]
fn a_kind_of_damage_the_shield_does_not_resist_at_all_goes_through_to_the_armor() {
    // the shield's em resonance is 0: nothing of it is taken there (a layer that would take nothing is skipped), the armor takes it at 0.5
    let mut health = health();
    let applied = health.apply(0, [50.0, 0.0, 0.0, 0.0]).unwrap();
    assert_eq!(applied.taken.shield, 0.0);
    assert_eq!(applied.taken.armor, 25.0);
    assert_eq!(applied.after.shield, 220.0);
}

#[test]
fn what_is_kept_of_a_hit_is_rounded_to_six_places_and_what_is_told_of_it_is_not() {
    let mut health = health();
    let applied = health.apply(0, [0.0, 22.680704, 0.0, 0.0]).unwrap();
    // 22.680704 * 0.8 points are taken: 220 - 18.1445632 = 201.8554368; the fraction kept is that over 220, rounded: 0.917525
    assert!((applied.after.shield - 201.8554368).abs() < 1e-9);
    assert_eq!(health.condition_at(0).shield_charge, 0.917525);
    // the pilot is told 201.855437 was left and, a tick later, that it was 201.8555 (the stored fraction) before
    assert_eq!(applied.attribute_changes(), [(SHIELD_CHARGE, 201.855437, 220.0)]);
    assert_eq!(health.shield_told(crate::modules::TICK), Some((201.865_819, 201.8555)));
}

#[test]
fn armor_and_hull_damage_are_told_in_points_as_it_grows() {
    let mut health = health();
    let applied = health.apply(0, [0.0, 0.0, 0.0, 1_000.0]).unwrap();
    // 440 explosive empty the shield; the 560 left do 56 points to the armor
    let changes = applied.attribute_changes();
    assert_eq!(changes[0], (SHIELD_CHARGE, 0.0, 220.0));
    assert_eq!(changes[1].0, ARMOR_DAMAGE);
    // what is told of the armor is the fraction kept (to six places) of its points: a few millionths off the 56 that were taken
    assert!((changes[1].1 - 56.0).abs() < 1e-4 && changes[1].2 == 0.0);
    let applied = health.apply(0, [0.0, 0.0, 0.0, 2_000.0]).unwrap();
    let changes = applied.attribute_changes();
    // the armor is gone (137.5 in all, 56 already) and the rest of the hit goes through to the hull at 0.33
    assert_eq!(changes.iter().map(|c| c.0).collect::<Vec<_>>(), [ARMOR_DAMAGE, HULL_DAMAGE]);
    assert!((changes[0].1 - 137.5).abs() < 1e-4 && (changes[0].2 - 56.0).abs() < 1e-4);
    assert!(changes[1].1 > 0.0 && changes[1].2 == 0.0);
}

#[test]
fn the_shield_recharges_along_the_curve_the_reference_shows() {
    // the recording: 201.855437 left at the hit, 201.865819 a tenth of a second on (the stored fraction 0.917525 is what it goes on from)
    let mut health = health();
    health.apply(0, [0.0, 22.680704, 0.0, 0.0]);
    let at = |ticks: i64| 220.0 * health.shield_ratio_at(ticks * SECOND / 10);
    assert!((at(1) - 201.865_819).abs() < 2e-6, "{}", at(1));
    // and 0.05 a half second on: the recorded 201.917433 is five ticks later (the reference's ticks are not exactly a tenth of a second apart)
    assert!((at(6) - 201.917_433).abs() < 5e-4, "{}", at(6));
    assert!(at(50) > at(6) && at(600) > at(50));
}

#[test]
fn an_empty_shield_recharges_from_one_point() {
    let mut health = health();
    health.apply(0, [0.0, 0.0, 0.0, 440.0]);
    assert_eq!(health.shield_ratio_at(0), 0.0);
    // a second on, from the point it is seeded with (a two hundred and twentieth of it): 1.166805 points, as a mended-at-once shield was told
    assert!(
        (220.0 * health.shield_ratio_at(SECOND) - 1.166_805).abs() < 1e-5,
        "{}",
        220.0 * health.shield_ratio_at(SECOND)
    );
}

#[test]
fn a_shield_within_a_point_of_full_is_full() {
    let mut health = health();
    health.apply(0, [0.0, 10.0, 0.0, 0.0]);
    // it is a long time later: the curve has all but reached the top
    assert_eq!(health.shield_ratio_at(3_000 * SECOND), 1.0);
    assert!(health.shield_ratio_at(10 * SECOND) < 1.0);
}

#[test]
fn the_pilot_is_told_the_shield_from_the_tick_after_a_hit_and_not_when_it_was_full_a_tick_before() {
    let mut health = health();
    assert_eq!(health.next_sample, None);
    health.apply(0, [0.0, 50.0, 0.0, 0.0]);
    assert_eq!(health.next_sample, Some(crate::modules::TICK));
    // once full nothing is told
    assert_eq!(health.shield_told(4_000 * SECOND), None);
}

#[test]
fn a_mended_ship_is_whole_and_says_what_changed() {
    let mut health = health();
    health.apply(0, [0.0, 0.0, 0.0, 1_000.0]);
    health.apply(0, [0.0, 0.0, 0.0, 2_000.0]);
    let mended = health.restore(SECOND);
    assert!(mended.changed);
    assert_eq!(
        health.state_at(SECOND),
        DamageState { shield: 1.0, shield_tau_ms: 172_000.0, armor: 1.0, structure: 1.0 }
    );
    let changes = mended.attribute_changes();
    assert_eq!(changes.iter().map(|c| c.0).collect::<Vec<_>>(), [SHIELD_CHARGE, ARMOR_DAMAGE, HULL_DAMAGE]);
    // the empty shield had recharged for a second from one point
    assert!(changes[0].1 == 220.0 && changes[0].2 > 1.0 && changes[0].2 < 2.0, "{changes:?}");
    assert_eq!(changes[1].1, 0.0);
    assert_eq!(changes[2].1, 0.0);
    // mended again there is nothing to say
    let again = health.restore(2 * SECOND);
    assert!(!again.changed && again.attribute_changes().is_empty());
}

#[test]
fn a_ship_that_comes_in_hurt_is_as_hurt_as_it_was_told() {
    let hurt = Condition { damage: 0.35, armor_damage: 0.6, shield_charge: 1.0, charge: 1.0 };
    let health = Health::new(frigate(), 860_000.0, hurt, 0);
    // the recording: armor 0.4 and hull 0.65 left; 82.5 points of armor and 70.4375 of hull gone
    let state = health.state_at(0);
    assert_eq!((state.shield, state.armor, state.structure), (1.0, 0.4, 0.65));
    let layers = health.layers_at(0);
    assert!((137.5 - layers.armor - 82.5).abs() < 1e-9 && (201.25 - layers.structure - 70.4375).abs() < 1e-9);
}

#[test]
fn new_numbers_keep_absolute_layer_damage_and_capacitor_fraction() {
    let mut health = health();
    health.apply(0, [0.0, 0.0, 0.0, 1_000.0]);
    let before = health.condition_at(SECOND);
    let old = health.vitals.layers();
    let hull_damage = old.structure * before.damage;
    let armor_damage = old.armor * before.armor_damage;
    let shield_damage = old.shield * (1.0 - before.shield_charge);
    let capacitor_fraction = before.charge;
    let mut bigger = health.vitals;
    bigger.shield_capacity *= 2.0;
    bigger.armor_hp *= 2.0;
    bigger.structure_hp *= 2.0;

    health.respec(bigger, 860_000.0, SECOND);

    let after = health.condition_at(SECOND);
    let new = health.vitals.layers();
    assert!((new.structure * after.damage - hull_damage).abs() < 1e-9);
    assert!((new.armor * after.armor_damage - armor_damage).abs() < 1e-9);
    assert!((new.shield * (1.0 - after.shield_charge) - shield_damage).abs() < 1e-9);
    assert_eq!(after.charge, capacitor_fraction);
}

#[test]
fn negative_hull_after_respec_waits_until_the_next_hull_damage() {
    let condition = Condition { damage: 0.9, armor_damage: 1.0, shield_charge: 0.0, charge: 1.0 };
    let original = frigate();
    let mut health = Health::new(original, 860_000.0, condition, 0);
    let mut smaller = health.vitals;
    smaller.structure_hp *= 0.8;

    health.respec(smaller, 860_000.0, 0);

    assert!(health.condition_at(0).damage > 1.0);
    assert_eq!(health.layers_at(0).structure, 0.0);
    let hit = health.apply(SECOND, [1.0, 0.0, 0.0, 0.0]).expect("the next hull hit is processed");
    assert!(hit.destroyed);
}

#[test]
fn a_shield_that_starts_below_full_is_told_from_the_start() {
    let health = Health::new(frigate(), 860_000.0, Condition { shield_charge: 0.7, ..Condition::FULL }, 100);
    assert_eq!(health.next_sample, Some(100));
    assert!((health.layers_at(100).shield - 154.0).abs() < 1e-9);
}

// ---- in a scene ----

use std::sync::Arc;

use crate::scene::tests::{NOON, STATION, spec, system};
use crate::ship::ShipSpec;

const A: SessionId = 7;
const B: SessionId = 8;

fn other_spec() -> ShipSpec {
    let mut other = spec();
    other.ship_id += 4;
    other.character_id += 1;
    other.owner_id += 1;
    other
}

/// Two ships that have bound the ballpark, forty seconds after they undocked (their protection is over).
fn crowd() -> (Scene, i64) {
    let mut scene = Scene::new(Arc::new((*system()).clone()));
    scene.undock(A, spec(), STATION, NOON).unwrap();
    scene.bind(A, NOON);
    scene.undock(B, other_spec(), STATION, NOON).unwrap();
    scene.bind(B, NOON);
    let t = NOON + 40 * SECOND;
    scene.poll(t);
    (scene, t)
}

fn poll_until(scene: &mut Scene, from: i64, seconds: f64) -> (Vec<Out>, i64) {
    let mut out = Vec::new();
    let mut t = from;
    while t < from + (seconds * SECOND as f64) as i64 {
        t += SECOND / 4;
        out.extend(scene.poll(t));
    }
    (out, t)
}

/// The lines of the `OnModuleAttributeChanges` sent to `session`: `(attribute, new, old)`, one list a notification.
fn attribute_lists(out: &[Out], session: SessionId) -> Vec<Vec<(i64, Value, Value)>> {
    out.iter()
        .filter_map(|o| match o {
            Out::Notify { session: s, name: "OnModuleAttributeChanges", args, .. } if *s == session => {
                let Some(Value::List(lines)) = args.first() else { panic!("{args:?}") };
                Some(
                    lines
                        .iter()
                        .map(|line| {
                            let Value::Tuple(t) = line else { panic!() };
                            let Value::Int(attribute) = t[3] else { panic!("{:?}", t[3]) };
                            (attribute, t[5].clone(), t[6].clone())
                        })
                        .collect(),
                )
            }
            _ => None,
        })
        .collect()
}

/// The damage states told in `DoDestinyUpdate`s, `(session, ship, shield, armor, structure)`.
fn damage_states(out: &[Out]) -> Vec<(SessionId, Value, Value, Value, Value)> {
    let mut states = Vec::new();
    for o in out {
        let Out::Notify { session, name: "DoDestinyUpdate", args, .. } = o else { continue };
        let Some(Value::List(actions)) = args.first() else { continue };
        for action in actions {
            let Value::Tuple(pair) = action else { continue };
            let Value::Tuple(inner) = &pair[1] else { continue };
            if inner[0] != Value::Str("OnDamageStateChange".into()) {
                continue;
            }
            let Value::Tuple(arguments) = &inner[1] else { continue };
            let Value::Tuple(state) = &arguments[1] else { continue };
            let Value::Tuple(shield) = &state[0] else { continue };
            states.push((
                *session,
                arguments[0].clone(),
                shield[0].clone(),
                state[1].clone(),
                state[2].clone(),
            ));
        }
    }
    states
}

fn ship_of(spec: &ShipSpec) -> i64 {
    spec.ship_id
}

#[test]
fn a_hit_tells_the_pilot_what_changed_and_everyone_who_can_see_the_ship_its_damage_state() {
    let (mut scene, t) = crowd();
    let (out, applied) = scene.damage(ship_of(&spec()), [0.0, 22.680704, 0.0, 0.0], t);
    assert!(applied.is_some());
    assert_eq!(
        attribute_lists(&out, A),
        [vec![(i64::from(SHIELD_CHARGE), Value::Real(201.855437), Value::Int(220))]],
        "the pilot is told the shield's charge, a whole number as an integer"
    );
    assert!(attribute_lists(&out, B).is_empty(), "the other pilot is not told the ship's attributes");
    let states = damage_states(&out);
    assert_eq!(
        states.iter().map(|s| s.0).collect::<Vec<_>>(),
        [A, B],
        "the ship's own pilot and the one that looks on"
    );
    let (_, ship, shield, armor, structure) = &states[0];
    assert_eq!(*ship, crate::wire::id(ship_of(&spec())));
    assert_eq!((armor, structure), (&Value::Real(1.0), &Value::Real(1.0)));
    assert_eq!(*shield, Value::Real(0.917525));
}

#[test]
fn a_hit_the_ship_does_not_feel_says_nothing() {
    let (mut scene, t) = crowd();
    let (out, applied) = scene.damage(ship_of(&spec()), [0.0; 4], t);
    assert!(out.is_empty() && applied.is_some_and(|a| !a.changed));
    let (out, applied) = scene.damage(99, [10.0; 4], t);
    assert!(out.is_empty() && applied.is_none(), "no such ship");
}

#[test]
fn a_hit_that_goes_through_the_armor_tells_the_armor_and_the_hull_in_points() {
    let (mut scene, t) = crowd();
    let (out, applied) = scene.damage(ship_of(&spec()), [0.0, 0.0, 0.0, 1_000.0], t);
    assert!(applied.is_some());
    let lists = attribute_lists(&out, A);
    assert_eq!(lists.len(), 1, "one notification for all the attributes");
    assert_eq!(lists[0].iter().map(|l| l.0).collect::<Vec<_>>(), [264, 266]);
    let states = damage_states(&out);
    let Value::Real(armor) = &states[0].3 else { panic!() };
    assert!(*armor < 1.0 && (*armor - (1.0 - 56.0 / 137.5)).abs() < 1e-5);
}

#[test]
fn the_shield_is_told_a_tick_after_a_hit_and_then_every_half_second_while_it_recharges() {
    let (mut scene, t) = crowd();
    scene.damage(ship_of(&spec()), [0.0, 22.680704, 0.0, 0.0], t);
    // the poll at t + 0.25 s takes in the moments at 0.1 s and 0.6 s? no: 0.1 s only; the next is 0.6 s
    let (out, _) = poll_until(&mut scene, t, 3.0);
    let told = attribute_lists(&out, A);
    assert_eq!(told.len(), 6, "at 0.1, 0.6, 1.1, 1.6, 2.1 and 2.6 s: {told:?}");
    // the first: the shield a tenth of a second on and what the stored fraction was
    assert_eq!(told[0], [(264, Value::Real(201.865819), Value::Real(201.8555))]);
    assert!(told.iter().all(|l| l.len() == 1 && l[0].0 == 264));
    assert!(attribute_lists(&out, B).is_empty());
    // and no damage state is sent: the client works the recharge out itself
    assert!(damage_states(&out).is_empty());
}

#[test]
fn the_stream_stops_when_the_shield_is_full() {
    let (mut scene, t) = crowd();
    scene.damage(ship_of(&spec()), [0.0, 5.0, 0.0, 0.0], t);
    // a long time on: the shield is back and nothing more is told
    let (_, t2) = poll_until(&mut scene, t, 1.0);
    let (out, t3) = poll_until(&mut scene, t2, 1_000.0);
    assert!(!attribute_lists(&out, A).is_empty());
    let (later, _) = poll_until(&mut scene, t3, 5.0);
    assert!(attribute_lists(&later, A).is_empty(), "{later:?}");
    assert_eq!(scene.pilots[&A].ship.health.state_at(t3).shield, 1.0);
    // and the ship is no longer looked at for a stream that has ended
    assert_eq!(scene.pilots[&A].ship.health.next_sample, None);
}

#[test]
fn a_heal_tells_the_pilot_then_the_capacitor_then_everyone_the_damage_state_and_answers_last() {
    let (mut scene, t) = crowd();
    scene.damage(ship_of(&spec()), [0.0, 0.0, 0.0, 1_000.0], t);
    scene.damage(ship_of(&spec()), [0.0, 0.0, 0.0, 1_000.0], t + SECOND);
    let out = scene.heal(A, t + 2 * SECOND);
    let names: Vec<String> = out
        .iter()
        .map(|o| match o {
            Out::Notify { session, name, .. } => format!("{session}:{name}"),
            Out::Accepted { session, command, .. } => format!("{session}:accepted {command}"),
            other => format!("{other:?}"),
        })
        .collect();
    assert_eq!(
        names,
        [
            "7:OnModuleAttributeChanges",
            "7:OnModuleAttributeChanges",
            "7:DoDestinyUpdate",
            "8:DoDestinyUpdate",
            "7:accepted 18"
        ]
    );
    let lists = attribute_lists(&out, A);
    assert!(
        lists[0].iter().map(|l| l.0).eq([264, 266, 3])
            || lists[0].iter().map(|l| l.0).eq([266, 3])
            || lists[0].iter().map(|l| l.0).eq([264, 266])
    );
    // the capacitor: 149.5 and 149.5 as it was (it was not touched)
    assert_eq!(lists[1], [(18, Value::Real(149.5), Value::Real(149.5))]);
    assert!(
        damage_states(&out)
            .iter()
            .all(|s| s.2 == Value::Real(1.0) && s.3 == Value::Real(1.0) && s.4 == Value::Real(1.0))
    );
    assert_eq!(scene.pilots[&A].ship.health.state_at(t + 2 * SECOND).structure, 1.0);
}

#[test]
fn a_heal_of_a_whole_ship_tells_only_the_capacitor_and_no_damage_state() {
    let (mut scene, t) = crowd();
    let out = scene.heal(A, t);
    assert_eq!(attribute_lists(&out, A).len(), 1);
    assert!(damage_states(&out).is_empty());
    assert!(matches!(out.last(), Some(Out::Accepted { command: 18, .. })));
}

#[test]
fn a_heal_fills_a_capacitor_that_was_low_and_stops_its_stream() {
    let (mut scene, t) = crowd();
    scene.activate(A, crate::modules::tests::afterburner(), t);
    let out = scene.heal(A, t + SECOND / 2);
    let lists = attribute_lists(&out, A);
    let last = lists.last().unwrap();
    assert_eq!(last[0].0, 18);
    let Value::Real(new) = last[0].1 else { panic!() };
    assert_eq!(new, 149.5);
    let (later, _) = poll_until(&mut scene, t + SECOND, 3.0);
    assert!(
        attribute_lists(&later, A).iter().all(|l| l[0].0 != 18),
        "the capacitor is full: nothing more is told"
    );
}

#[test]
fn a_heal_of_a_ship_that_is_not_here_is_refused() {
    let (mut scene, t) = crowd();
    assert_eq!(scene.heal(99, t), [Out::Refused { session: 99, command: 18, reason: "NO_SHIP_IN_SPACE" }]);
    assert_eq!(
        scene.condition(99, t),
        [Out::Refused { session: 99, command: 19, reason: "NO_SHIP_IN_SPACE" }]
    );
}

#[test]
fn the_condition_is_what_the_ship_has_taken_and_how_full_its_capacitor_is() {
    let (mut scene, t) = crowd();
    scene.damage(ship_of(&spec()), [0.0, 22.680704, 0.0, 1_000.0], t);
    let Out::Condition { session, command, condition } = scene.condition(A, t).remove(0) else { panic!() };
    assert_eq!((session, command), (A, 19));
    assert_eq!(condition.charge, 1.0);
    assert_eq!(condition.shield_charge, 0.0, "the explosive damage emptied the shield");
    assert!(condition.armor_damage > 0.0 && condition.damage == 0.0);
}

#[test]
fn a_ship_that_docks_hurt_hands_over_what_it_has_taken() {
    let mut scene = Scene::new(Arc::new((*system()).clone()));
    let mut near = spec();
    near.max_velocity = 0.0;
    scene.undock(A, near, STATION, NOON).unwrap();
    // 500 m off the surface of the station: in docking range
    scene.pilots.get_mut(&A).unwrap().ship.position =
        eve_destiny::Vec3::new(1_000_000.0 + 1_000.0 + 47.0 + 500.0, 0.0, 0.0);
    scene.bind(A, NOON);
    // hurt at the station's door, it is told to dock and is in the station four seconds on
    scene.damage(ship_of(&spec()), [0.0, 0.0, 0.0, 1_000.0], NOON);
    scene.command(A, crate::scene::Command::Dock { station_id: STATION }, NOON);
    let (out, _) = poll_until(&mut scene, NOON, 6.0);
    let docked = out
        .iter()
        .find_map(|o| match o {
            Out::Docked { session: A, condition, .. } => Some(*condition),
            _ => None,
        })
        .expect("the ship docked");
    assert!(docked.armor_damage > 0.4 && docked.shield_charge < 0.5, "{docked:?}");
    assert_eq!(docked.charge, 1.0);
}

#[test]
fn new_numbers_for_a_ship_in_space_keep_absolute_damage_and_capacitor_fraction() {
    let (mut scene, t) = crowd();
    scene.damage(ship_of(&spec()), [0.0, 0.0, 0.0, 1_000.0], t);
    let before = scene.pilots[&A].ship.health.condition_at(t + SECOND);
    let old = scene.pilots[&A].ship.health.vitals.layers();
    let hull_damage = old.structure * before.damage;
    let armor_damage = old.armor * before.armor_damage;
    let shield_damage = old.shield * (1.0 - before.shield_charge);
    let capacitor_fraction = before.charge;
    let mut bigger = spec();
    bigger.vitals.shield_capacity *= 2.0;
    bigger.vitals.armor_hp *= 2.0;
    bigger.vitals.structure_hp *= 2.0;

    scene.respec(A, bigger, t + SECOND);

    let health = &scene.pilots[&A].ship.health;
    let after = health.condition_at(t + SECOND);
    let new = health.vitals.layers();
    assert!((new.structure * after.damage - hull_damage).abs() < 1e-9);
    assert!((new.armor * after.armor_damage - armor_damage).abs() < 1e-9);
    assert!((new.shield * (1.0 - after.shield_charge) - shield_damage).abs() < 1e-9);
    assert_eq!(after.charge, capacitor_fraction);
}

#[test]
fn a_ship_that_comes_in_hurt_is_shown_hurt() {
    let mut hurt = spec();
    hurt.condition = Condition { damage: 0.35, armor_damage: 0.6, shield_charge: 1.0, charge: 0.65 };
    let mut scene = Scene::new(Arc::new((*system()).clone()));
    scene.undock(A, hurt, STATION, NOON).unwrap();
    let state = scene.pilots[&A].ship.health.state_at(NOON);
    assert_eq!((state.shield, state.armor, state.structure), (1.0, 0.4, 0.65));
    let Out::Condition { condition, .. } = scene.condition(A, NOON).remove(0) else { panic!() };
    assert_eq!(condition.charge, 0.65);
    // a capacitor that starts low is told as it recharges
    let (out, _) = poll_until(&mut scene, NOON, 1.0);
    assert!(attribute_lists(&out, A).iter().any(|l| l[0].0 == 18), "{out:?}");
}

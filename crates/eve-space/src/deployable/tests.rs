//! The warp-disruption sources and the bombs of the simulation (`deployable`): a bubble holds the ships in its range unless they are immune, a cloak that is not
//! warp-safe holds its ship, a probe holds the ships in its range until its lifetime is up and is then taken out, and a bomb's blast reaches the ships within its
//! radius but not its launcher.

use super::*;
use crate::modules::{Action, Effect};
use crate::scene::tests::{NOON, STATION, spec, system};
use crate::scene::{Command, Out, Scene};
use crate::visibility::CloakState;

const A: SessionId = 7;
const B: SessionId = 8;
const SECOND: i64 = 10_000_000;
const MODULE: i64 = 9_988_400_001_940;
const PROBE_SPEC: fn() -> Spec =
    || Spec { charge_type_id: 22_778, group_id: 548, name: "Warp Disrupt Probe".to_string(), radius: 40.0 };

/// A ship of its own: the scene's spec, with its ids moved on.
fn other_spec() -> crate::ship::ShipSpec {
    let mut other = spec();
    other.ship_id += 4;
    other.character_id += 1;
    other.owner_id += 1;
    other
}

/// Two ships in a system, five kilometres apart, both bound: `A` and `B`, with the clock thirty seconds after they undocked.
fn crowd() -> (Scene, i64) {
    let mut scene = Scene::new(system());
    scene.undock(A, spec(), STATION, NOON).unwrap();
    scene.bind(A, NOON);
    scene.undock(B, other_spec(), STATION, NOON).unwrap();
    scene.bind(B, NOON);
    let a = scene.pilots[&A].ship.position;
    place(&mut scene, B, eve_destiny::Vec3::new(a.x + 5_000.0, a.y, a.z));
    let t = NOON + 40 * SECOND;
    scene.poll(t);
    (scene, t)
}

fn place(scene: &mut Scene, session: SessionId, position: eve_destiny::Vec3) {
    let pilot = scene.pilots.get_mut(&session).unwrap();
    pilot.ship.position = position;
    pilot.ship.velocity = eve_destiny::Vec3::ZERO;
    pilot.ship.speed_fraction = 0.0;
    pilot.ship.course = crate::ship::Course::Stop;
    pilot.protected_until = 0;
}

/// A module's effect that runs one cycle of `duration_ms` and does `action`.
fn module(module_id: i64, action: Action, duration_ms: f64) -> Effect {
    Effect {
        burns_fuel: false,
        module_id,
        module_type: 1,
        effect_id: 1,
        fx: String::new(),
        duration_ms,
        capacitor_need: 0.0,
        repeat: 1,
        target_id: 0,
        turret: None,
        action: Some(action),
        on: None,
        off: None,
        on_changes: Vec::new(),
        off_changes: Vec::new(),
        rules: Default::default(),
    }
}

/// A warp to a point far off, which nothing blocks but the warp sources.
fn warp(scene: &mut Scene, session: SessionId, now: i64) -> Vec<Out> {
    scene.command(
        session,
        Command::WarpToPoint { destination: eve_destiny::Vec3::new(1.0e11, 0.0, 0.0), range: 0.0 },
        now,
    )
}

/// Polls the scene every quarter second for `seconds` from `from`: what it told, and the clock at the end.
fn poll_for(scene: &mut Scene, from: i64, seconds: f64) -> (Vec<Out>, i64) {
    let mut out = Vec::new();
    let mut t = from;
    while t < from + (seconds * SECOND as f64) as i64 {
        t += SECOND / 4;
        out.extend(scene.poll(t));
    }
    (out, t)
}

fn accepted(out: &[Out]) -> bool {
    out.iter().any(|o| matches!(o, Out::Accepted { .. }))
}

fn refused_with(out: &[Out], reason: &str) -> bool {
    out.iter().any(|o| matches!(o, Out::Refused { reason: r, .. } if *r == reason))
}

#[test]
fn a_bubble_holds_the_ships_in_its_range_and_lets_the_others_warp() {
    let (mut scene, t) = crowd();
    let started = scene.activate(A, module(MODULE, Action::WarpBubble { range: 16_000.0 }, 30_000.0), t);
    assert!(accepted(&started), "{started:?}");
    let held = warp(&mut scene, B, t + SECOND);
    assert!(refused_with(&held, "WARP_DISRUPTED_BY_BUBBLE"), "{held:?}");
    // a ship beyond the field's range is not in it
    place(&mut scene, B, eve_destiny::Vec3::new(40_000.0, 0.0, 0.0));
    let free = warp(&mut scene, B, t + 2 * SECOND);
    assert!(accepted(&free), "{free:?}");
}

#[test]
fn an_immune_ship_is_not_held_by_a_bubble() {
    let (mut scene, t) = crowd();
    assert!(accepted(&scene.activate(
        A,
        module(MODULE, Action::WarpBubble { range: 16_000.0 }, 30_000.0),
        t
    )));
    assert!(accepted(&scene.activate(B, module(MODULE + 1, Action::WarpImmunity, 10_000.0), t + SECOND)));
    let through = warp(&mut scene, B, t + 2 * SECOND);
    assert!(accepted(&through), "{through:?}");
}

#[test]
fn a_cloak_that_is_not_warp_safe_holds_its_ship_and_a_covert_one_does_not() {
    let (mut scene, t) = crowd();
    // a cycle of two seconds: the cloak runs until its cycle ends (the simulation keeps an effect to the end of its cycle)
    assert!(accepted(&scene.activate(B, module(MODULE, Action::Cloak { warp_safe: false }, 2_000.0), t)));
    assert_eq!(scene.pilots[&B].cloak, CloakState::Cloaked);
    let held = warp(&mut scene, B, t + SECOND);
    assert!(refused_with(&held, "WARP_BLOCKED_BY_CLOAK"), "{held:?}");
    let (_, t2) = poll_for(&mut scene, t + SECOND, 6.0);
    assert_eq!(scene.pilots[&B].cloak, CloakState::Uncloaked);
    assert!(accepted(&scene.activate(
        B,
        module(MODULE + 1, Action::Cloak { warp_safe: true }, 30_000.0),
        t2
    )));
    let covert = warp(&mut scene, B, t2 + SECOND);
    assert!(accepted(&covert), "{covert:?}");
}

#[test]
fn a_probe_holds_the_ships_in_its_range_until_its_lifetime_is_up_and_is_then_taken_out() {
    let (mut scene, t) = crowd();
    let probe = module(
        MODULE,
        Action::WarpProbe { ball: PROBE_SPEC(), range: 20_000.0, lifetime_ms: 120_000.0 },
        10_000.0,
    );
    let released = scene.activate(A, probe, t);
    assert!(accepted(&released), "{released:?}");
    assert_eq!(scene.deployables.len(), 1);
    let held = warp(&mut scene, B, t + SECOND);
    assert!(refused_with(&held, "WARP_DISRUPTED_BY_BUBBLE"), "{held:?}");
    let (gone, t2) = poll_for(&mut scene, t + SECOND, 125.0);
    assert!(scene.deployables.is_empty());
    assert!(
        gone.iter().any(|o| format!("{o:?}").contains("RemoveBalls")),
        "the probe is taken out of the ballpark of the pilots who saw it"
    );
    let free = warp(&mut scene, B, t2);
    assert!(accepted(&free), "{free:?}");
}

#[test]
fn a_bomb_goes_off_where_it_is_and_hurts_the_ships_in_its_blast_but_not_its_launcher() {
    let (mut scene, t) = crowd();
    // both ships hold still: the bomb goes off where the launcher released it, and the target is 200 m from there
    let a = scene.pilots[&A].ship.position;
    place(&mut scene, A, a);
    place(&mut scene, B, eve_destiny::Vec3::new(a.x + 200.0, a.y, a.z));
    let bomb = Action::Bomb {
        ball: Spec { charge_type_id: 27_920, group_id: 90, name: "Electron Bomb".to_string(), radius: 40.0 },
        kind: BombKind::Damage,
        lifetime_ms: 12_000.0,
        blast_radius: 400.0,
        damage: [5_800.0, 0.0, 0.0, 0.0],
        ecm: [0.0; 4],
        neutralize: 0.0,
    };
    let released = scene.activate(A, module(MODULE, bomb, 10_000.0), t);
    assert!(accepted(&released), "{released:?}");
    let (out, _) = poll_for(&mut scene, t, 14.0);
    assert!(scene.deployables.is_empty(), "the bomb went off");
    // a hit tells its attacker and its target once each (the attacker's copy and the target's); a launcher hit by its own blast would be told twice
    let told = |who: SessionId| {
        out.iter()
            .filter(|o| matches!(o, Out::Notify { session, name: "OnDamageMessage", .. } if *session == who))
            .count()
    };
    assert_eq!((told(B), told(A)), (1, 1), "the blast hits the ship in its radius, and not its launcher");
    assert!(out.iter().any(|o| format!("{o:?}").contains("TerminalPlayDestructionEffect")));
}

#[test]
fn a_stasis_burst_webs_the_ships_in_its_range_for_its_duration_and_spares_its_own_ship() {
    let (mut scene, t) = crowd();
    let base = scene.pilots[&B].ship.spec.max_velocity;
    let own = scene.pilots[&A].ship.spec.max_velocity;
    let burst = Action::StasisBurst { range: 15_000.0, duration_ms: 30_000.0, speed_factor: -20.0 };
    let started = scene.activate(A, module(MODULE, burst, 10_000.0), t);
    assert!(accepted(&started), "{started:?}");
    let webbed = scene.pilots[&B].ship.spec.max_velocity;
    assert!((webbed - base * 0.8).abs() < 1e-6, "webbed to {webbed}, from {base}");
    assert_eq!(scene.pilots[&A].ship.spec.max_velocity, own, "the launcher is not webbed by its own burst");
    // the web lasts its duration from the start of the cycle, and then the ship is free
    let (_, _) = poll_for(&mut scene, t, 40.0);
    assert!((scene.pilots[&B].ship.spec.max_velocity - base).abs() < 1e-6);
}

#[test]
fn a_defender_launcher_shoots_down_the_hostile_missile_in_its_reach() {
    use crate::missile_entity::{MISSILE_ID_BASE, MissileEntity, Shooter};
    use crate::missiles::Missile;
    let (mut scene, t) = crowd();
    // a missile of another owner flies from B at A: five kilometres off, with little hit points
    let incoming = Missile {
        charge_type_id: 27_000,
        group_id: 385,
        name: "Heavy Missile".to_string(),
        radius: 1.0,
        mass: 1.0,
        agility: 1.0,
        hit_points: 5.0,
        max_velocity: 1_000.0,
        flight_time_ms: 10_000.0,
        explosion_radius: 10.0,
        explosion_velocity: 400.0,
        damage_reduction_factor: 0.1,
        damage_reduction_sensitivity: 5.5,
    };
    let shooter = Shooter {
        session: B,
        ship_id: scene.pilots[&B].ship.spec.ship_id,
        module_id: MODULE + 300,
        weapon_type_id: 1,
        owner_id: 999,
        corporation_id: 0,
        alliance_id: 0,
    };
    let attacker = scene.kinematics_of(B).unwrap();
    let target = scene.kinematics_of(A).unwrap();
    let mut missile = MissileEntity::launch(
        MISSILE_ID_BASE + 1,
        shooter,
        scene.pilots[&A].ship.spec.ship_id,
        incoming,
        [0.0; 4],
        &attacker,
        &target,
        t,
    );
    // the pilot of the launcher sees it, and is told when it is gone
    missile.watchers.insert(A);
    scene.missiles.insert(MISSILE_ID_BASE + 1, missile);
    let defender = Missile {
        charge_type_id: 32_782,
        group_id: 88,
        name: "Defender Missile I".to_string(),
        radius: 1.0,
        mass: 1.0,
        agility: 1.0,
        hit_points: 5.0,
        max_velocity: 10_000.0,
        flight_time_ms: 3_000.0,
        explosion_radius: 10.0,
        explosion_velocity: 400.0,
        damage_reduction_factor: 0.1,
        damage_reduction_sensitivity: 5.5,
    };
    let launcher = Effect {
        module_id: MODULE + 301,
        ..module(MODULE + 301, Action::Defender { missile: defender, damage: [200.0; 4] }, 10_000.0)
    };
    let started = scene.activate(A, launcher, t + SECOND);
    assert!(accepted(&started), "{started:?}");
    assert!(!scene.missiles.contains_key(&(MISSILE_ID_BASE + 1)), "the incoming missile is shot down");
    assert!(
        started.iter().any(|o| format!("{o:?}").contains("TerminalPlayDestructionEffect")),
        "the interception is played as an explosion"
    );
}

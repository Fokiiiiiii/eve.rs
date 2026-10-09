use super::*;

const SECOND: i64 = TICKS_PER_SECOND;

fn kin(position: [f64; 3], velocity: [f64; 3], radius: f64) -> Kinematics {
    Kinematics { position, velocity, radius, signature_radius: radius }
}

/// The scourge rocket of the reference's recording (`oracle/fixtures/scenarios/missile.json`): 2250 m/s, two seconds of flight, the ball of 300 m radius and 100 kg.
fn scourge() -> Missile {
    Missile {
        charge_type_id: 266,
        group_id: 387,
        name: "Scourge Rocket".to_string(),
        radius: 300.0,
        mass: 100.0,
        agility: 5.220_125_693_558_398,
        hit_points: 90.0,
        max_velocity: 2_250.0,
        flight_time_ms: 2_000.0,
        explosion_radius: 20.0,
        explosion_velocity: 150.0,
        damage_reduction_factor: 0.644,
        damage_reduction_sensitivity: 5.5,
        dot: None,
    }
}

fn shooter() -> Shooter {
    Shooter {
        session: 7,
        ship_id: 9_988_400_003_581,
        module_id: 9_988_400_003_586,
        weapon_type_id: 10_629,
        owner_id: 140_000_465,
        corporation_id: 1_000_006,
        alliance_id: 0,
    }
}

fn fired(attacker: &Kinematics, target: &Kinematics, spec: Missile) -> MissileEntity {
    MissileEntity::launch(
        MISSILE_ID_BASE,
        shooter(),
        980_050_000_111,
        spec,
        [0.0, 0.0, 38.115, 0.0],
        attacker,
        target,
        0,
    )
}

fn at_rest(x: f64) -> Kinematics {
    kin([x, 0.0, 0.0], [0.0; 3], 50.0)
}

/// The `AddBalls2` of the first missile the reference launched in the recording, byte for byte, the ball of the entity this builds.
#[test]
fn the_ball_of_a_missile_is_the_one_the_reference_launched() {
    let recorded = "012e8abf6a00800eb4e0eb0e000500009643e77da3cf02295242d4a8b21cdb5708c29bf160716c3c424205000000000000594000ffffffffffffffff46420f000000000000a00c45000000000000000000000000000000000000000000000000450ba74000000000ffef38882fe40000000000afc3fdb9089b150900000000000056b266d302295242204b5ad3da5708c2e4cde0716c3c4242";
    let launch = Vec3::new(311_989_845_645.967_2, -13_069_149_078.332_436, 156_646_302_401.887_54);
    let target = Vec3::new(311_989_849_498.786_5, -13_069_146_731.286_682, 156_646_302_657.608_52);
    let missile = MissileEntity {
        id: MISSILE_ID_BASE,
        shooter: shooter(),
        target: 980_050_000_111,
        spec: scourge(),
        raw_damage: [0.0, 0.0, 38.115, 0.0],
        launch_position: launch,
        position: launch,
        velocity: REST,
        direction: unit(sub(target, launch), Vec3::new(1.0, 0.0, 0.0)),
        launched_at: 0,
        stepped_at: 0,
        expires_at: 0,
        impact_due: 0,
        spreads: true,
        phase: Phase::Flying,
        hit_points: 90.0,
        target_seen: target,
        target_at_launch: target,
        watchers: BTreeSet::new(),
    };
    // the target is a rat of 50 m: the missile follows it at minus the two radii, 300 + 50
    let buffer = payload::add_balls_buffer(1_790_937_646, &[missile.ball(50.0)]).unwrap();
    let Value::Buffer(bytes) = buffer else { panic!("{buffer:?}") };
    let hex: String = bytes.iter().map(|byte| format!("{byte:02x}")).collect();
    assert_eq!(hex, recorded);
}

#[test]
fn the_slim_item_and_the_launch_are_what_the_client_was_sent() {
    let (attacker, target) = (at_rest(0.0), at_rest(4_500.0));
    let missile = fired(&attacker, &target, scourge());
    let keys: Vec<&str> = missile.slim_entries().iter().map(|(k, _)| *k).collect();
    assert_eq!(
        keys,
        ["itemID", "typeID", "ownerID", "name", "groupID", "categoryID", "sourceShipID", "launchModules"]
    );
    let entries = missile.slim_entries();
    assert_eq!(entries[1].1, int(266));
    assert_eq!(entries[3].1, text("Scourge Rocket"));
    assert_eq!((entries[4].1.clone(), entries[5].1.clone()), (int(387), int(8)));
    assert_eq!(entries[7].1, list([id(9_988_400_003_586)]));
    let actions = launch_actions(1_790_937_646, &missile, 50.0).unwrap();
    assert_eq!(actions.len(), 2, "the ball and the launch are told together");
    let launched = payload::action(
        1_790_937_646,
        "LaunchMissile",
        [id(MISSILE_ID_BASE), id(980_050_000_111), id(9_988_400_003_581), int(1), int(1)],
    );
    assert_eq!(actions[1], launched);
}

#[test]
fn a_missile_is_put_at_a_muzzle_in_front_of_the_ship_towards_the_target() {
    // a ship of 47 m is given a muzzle of 21 m (its radius times 0.45, between 12 and 90)
    let near = muzzle(&kin([0.0; 3], [0.0; 3], 47.0), Vec3::new(1_000.0, 0.0, 0.0));
    assert!((near.x - 21.15).abs() < 1e-9 && near.y == 0.0);
    assert_eq!(muzzle(&kin([0.0; 3], [0.0; 3], 5.0), Vec3::new(0.0, 1_000.0, 0.0)).y, 12.0);
    assert_eq!(muzzle(&kin([0.0; 3], [0.0; 3], 500.0), Vec3::new(0.0, 0.0, 1_000.0)).z, 90.0);
}

#[test]
fn a_missile_that_takes_1_6_seconds_or_more_to_get_there_spreads_and_a_nearer_target_does_not() {
    // 2250 m/s: 3.6 km of surface is 1.6 s
    assert!(fired(&at_rest(0.0), &at_rest(3_700.0 + 50.0 + 21.0), scourge()).spreads);
    assert!(!fired(&at_rest(0.0), &at_rest(3_000.0), scourge()).spreads);
    // what spreads starts at rest and what does not starts at its top speed
    let (far, near) = (
        fired(&at_rest(0.0), &at_rest(4_500.0), scourge()),
        fired(&at_rest(0.0), &at_rest(1_000.0), scourge()),
    );
    assert_eq!((far.ball(50.0).speed_fraction, far.ball(50.0).velocity), (0.0, REST));
    assert_eq!(near.ball(50.0).speed_fraction, 1.0);
    assert!((length(near.ball(50.0).velocity) - 2_250.0).abs() < 1e-6);
}

#[test]
fn a_missile_homes_on_its_target_and_reaches_it_when_it_gets_within_its_radius() {
    let mut missile = fired(&at_rest(0.0), &at_rest(2_250.0 + 21.15), scourge());
    let target = at_rest(2_250.0 + 21.15);
    // 2250 m of the target's centre less its radius of 50 is 2200 m from the muzzle of 21: a second less a hair
    let mut now = 0;
    let mut reached = None;
    while now < 2 * SECOND && reached.is_none() {
        now += SECOND / 10;
        if missile.advance(Some(&target), now) == Motion::Reached {
            reached = Some(now);
        }
    }
    let reached = reached.expect("it gets there");
    assert_eq!(
        reached, SECOND,
        "after the tenth step, which is the one that crosses the radius of the target"
    );
    // it stops where it touches the target's radius, not at its centre
    let distance = length(sub(missile.position, vec(target.position)));
    assert!((distance - 50.0).abs() < 1.0, "{distance}");
}

#[test]
fn a_missile_does_not_fly_through_a_target_in_one_long_step() {
    let mut missile = fired(&at_rest(0.0), &at_rest(1_000.0), scourge());
    // a step of ten seconds takes it twenty kilometres further than the target is: the swept test still finds it
    assert_eq!(missile.advance(Some(&at_rest(1_000.0)), 10 * SECOND), Motion::Reached);
    assert!(missile.position.x < 1_000.0, "it stopped at the surface: {:?}", missile.position);
}

#[test]
fn a_missile_turns_at_once_to_follow_a_target_that_moves() {
    let mut missile = fired(&at_rest(0.0), &at_rest(3_000.0), scourge());
    // the target runs off at a right angle: the missile's heading follows it, and the closing speed is the missile's own
    let mut now = 0;
    let mut y = 0.0;
    for _ in 0..3 {
        now += SECOND / 10;
        y += 100.0;
        let target = kin([3_000.0, y, 0.0], [0.0, 1_000.0, 0.0], 50.0);
        missile.advance(Some(&target), now);
        let toward = unit(sub(vec(target.position), missile.position), REST);
        assert!(dot(missile.direction, toward) > 0.999, "it is aimed at where the target is");
    }
    assert!(missile.position.y > 0.0, "it went after the target");
    assert!((length(missile.velocity) - 2_250.0).abs() < 1e-6);
}

#[test]
fn a_missile_that_cannot_catch_its_target_stalls_when_its_flight_time_is_up_short_of_it() {
    // the target is out of reach, and runs: 2 s of 2250 m/s is 4.5 km, the target is 20 km off
    let mut missile = fired(&at_rest(0.0), &at_rest(20_000.0), scourge());
    let target = at_rest(20_000.0);
    let mut now = 0;
    let outcome = loop {
        now += SECOND / 10;
        match missile.advance(Some(&target), now) {
            Motion::Flew => continue,
            other => break other,
        }
    };
    assert_eq!(outcome, Motion::RanOut);
    // the flight is two seconds and the time to clear the radius of the ship that fired it (a ship of 50 m: 50 / 2250 s)
    assert!(
        now >= missile.expires_at && now - missile.expires_at <= SECOND / 10,
        "{now} {}",
        missile.expires_at
    );
    assert!(
        missile.position.x < 4_800.0 && missile.position.x > 4_000.0,
        "it did not go beyond what it flies: {:?}",
        missile.position
    );
}

#[test]
fn a_step_is_never_longer_than_the_flight_time_the_missile_has_left() {
    let mut missile = fired(&at_rest(0.0), &at_rest(20_000.0), scourge());
    let target = at_rest(20_000.0);
    // one poll a long time after it was fired still only flies what it had: 2 s and a hair
    missile.advance(Some(&target), 60 * SECOND);
    assert!(missile.position.x < 4_700.0, "{:?}", missile.position);
}

#[test]
fn the_client_s_warhead_is_due_at_the_average_of_the_surface_and_the_centre_time() {
    let missile = fired(&at_rest(0.0), &at_rest(4_500.0), scourge());
    let muzzle = 22.5; // a ship of 50 m: 0.45 of its radius
    let (surface, centre) = ((4_500.0 - muzzle - 50.0) / 2_250.0, (4_500.0 - muzzle) / 2_250.0);
    let due = (surface + centre) / 2.0;
    assert!((seconds(missile.impact_due) - due).abs() < 1e-6, "{} {due}", seconds(missile.impact_due));
}

#[test]
fn a_missile_has_the_hit_points_of_its_charge_apart_from_any_ship() {
    let missile = fired(&at_rest(0.0), &at_rest(1_000.0), scourge());
    assert_eq!(missile.hit_points, 90.0);
    assert!(missile.is_flying());
}
